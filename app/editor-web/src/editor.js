import { Editor, Extension } from '@tiptap/core'
import StarterKit from '@tiptap/starter-kit'
import TextStyle from '@tiptap/extension-text-style'
import Underline from '@tiptap/extension-underline'
import Highlight from '@tiptap/extension-highlight'
import Link from '@tiptap/extension-link'
import Placeholder from '@tiptap/extension-placeholder'
import TaskList from '@tiptap/extension-task-list'
import TaskItem from '@tiptap/extension-task-item'
import BubbleMenu from '@tiptap/extension-bubble-menu'
import Image from '@tiptap/extension-image'
import Table from '@tiptap/extension-table'
import TableRow from '@tiptap/extension-table-row'
import TableHeader from '@tiptap/extension-table-header'
import TableCell from '@tiptap/extension-table-cell'
import { TableMap } from '@tiptap/pm/tables'
import { Markdown } from 'tiptap-markdown'
import TurndownService from 'turndown'
import { gfm } from 'turndown-plugin-gfm'
import CodeBlockLowlight from '@tiptap/extension-code-block-lowlight'
import { createLowlight } from 'lowlight'
import json from 'highlight.js/lib/languages/json'
import xml from 'highlight.js/lib/languages/xml'
import yaml from 'highlight.js/lib/languages/yaml'
import javascript from 'highlight.js/lib/languages/javascript'
import css from 'highlight.js/lib/languages/css'
import bash from 'highlight.js/lib/languages/bash'
import python from 'highlight.js/lib/languages/python'
import sql from 'highlight.js/lib/languages/sql'

const lowlight = createLowlight()
lowlight.register({ json, xml, yaml, javascript, css, bash, python, sql })

const host = window.chrome?.webview
let saveTimer = null
let loading = false

// Request/response bridge to C#: post a message, await the matching reply that C# sends
// back via a window.PaperNote.* callback. One outstanding call per type is enough — the
// meeting flow awaits each reply before sending the next.
const hostReplies = {}
function callHost(type, payload = {}) {
  return new Promise(resolve => {
    if (!host) { resolve(null); return }
    hostReplies[type] = resolve
    host.postMessage({ type, ...payload })
  })
}
function resolveHost(type, result) {
  const resolve = hostReplies[type]
  hostReplies[type] = null
  resolve?.(result)
}

// Load the mermaid bundle once, on first diagram render. Injected as a sibling script so
// it never touches the startup path.
let mermaidPromise = null
function loadMermaid() {
  if (window.__mermaid) return Promise.resolve(window.__mermaid)
  if (mermaidPromise) return mermaidPromise
  mermaidPromise = new Promise((resolve, reject) => {
    const s = document.createElement('script')
    s.src = 'mermaid.js'
    s.onload = () => resolve(window.__mermaid)
    s.onerror = () => reject(new Error('Could not load the diagram engine.'))
    document.head.appendChild(s)
  })
  return mermaidPromise
}

// Font size: inline on text (Docs-style), and on the list item so its bullet/number
// marker scales with the text too.
const FontSize = Extension.create({
  name: 'fontSize',
  addGlobalAttributes() {
    const sizeAttr = {
      fontSize: {
        default: null,
        parseHTML: el => el.style.fontSize || null,
        renderHTML: attrs => attrs.fontSize ? { style: `font-size:${attrs.fontSize}` } : {}
      }
    }
    return [{ types: ['textStyle', 'listItem'], attributes: sizeAttr }]
  },
  addCommands() {
    return {
      setFontSize: size => ({ chain }) =>
        chain().setMark('textStyle', { fontSize: size }).updateAttributes('listItem', { fontSize: size }).run(),
      unsetFontSize: () => ({ chain }) =>
        chain().setMark('textStyle', { fontSize: null }).updateAttributes('listItem', { fontSize: null }).removeEmptyTextStyle().run()
    }
  }
})

// Line height as a block attribute on paragraphs + headings.
const LineHeight = Extension.create({
  name: 'lineHeight',
  addOptions() { return { types: ['paragraph', 'heading'] } },
  addGlobalAttributes() {
    return [{
      types: this.options.types,
      attributes: {
        lineHeight: {
          default: null,
          parseHTML: el => el.style.lineHeight || null,
          renderHTML: attrs => attrs.lineHeight ? { style: `line-height:${attrs.lineHeight}` } : {}
        }
      }
    }]
  },
  addCommands() {
    return {
      setLineHeight: lh => ({ chain }) => {
        let c = chain().focus()
        this.options.types.forEach(t => { c = c.updateAttributes(t, { lineHeight: lh }) })
        return c.run()
      }
    }
  }
})

// surface any boot error to the host so we don't fail silently
window.addEventListener('error', e =>
  host?.postMessage({ type: 'error', msg: `${e.message} @ ${e.filename}:${e.lineno}` }))

// Image with a drag-to-resize handle. Width persists as an attribute on the <img>
// so it survives save/load. Aspect ratio stays fixed (height auto in CSS).
const ResizableImage = Image.extend({
  addAttributes() {
    return {
      ...this.parent?.(),
      width: {
        default: null,
        parseHTML: el => el.getAttribute('width') || (el.style.width ? parseInt(el.style.width) : null),
        renderHTML: attrs => attrs.width ? { width: attrs.width, style: `width:${attrs.width}px` } : {}
      }
    }
  },
  addNodeView() {
    return ({ node, editor, getPos }) => {
      const dom = document.createElement('div')
      dom.className = 'img-wrap'

      const img = document.createElement('img')
      img.src = node.attrs.src
      if (node.attrs.alt) img.alt = node.attrs.alt
      if (node.attrs.width) img.style.width = node.attrs.width + 'px'

      const handle = document.createElement('div')
      handle.className = 'img-resize-handle'

      handle.addEventListener('pointerdown', e => {
        e.preventDefault()
        const startX = e.clientX
        const startW = img.offsetWidth

        const move = ev => { img.style.width = Math.max(60, startW + ev.clientX - startX) + 'px' }
        const up = ev => {
          document.removeEventListener('pointermove', move)
          document.removeEventListener('pointerup', up)
          const width = Math.max(60, startW + ev.clientX - startX)
          if (typeof getPos === 'function')
            editor.commands.command(({ tr }) => {
              tr.setNodeMarkup(getPos(), undefined, { ...node.attrs, width })
              return true
            })
        }
        document.addEventListener('pointermove', move)
        document.addEventListener('pointerup', up)
      })

      dom.append(img, handle)
      return {
        dom,
        ignoreMutation: () => true,
        update: updated => {
          if (updated.type.name !== node.type.name) return false
          img.style.width = updated.attrs.width ? updated.attrs.width + 'px' : ''
          return true
        }
      }
    }
  }
})

// Code block that renders a live diagram when its language is "mermaid". Any other
// language behaves like a normal highlighted code block. Stored as a plain ```mermaid
// fenced block in Markdown, so the source is the truth and round-trips on save/load.
// ponytail: the resize width is view-only — Markdown has nowhere to keep it, so a diagram
// resets to its natural width after a reload. That's the plain-text-truth tradeoff.
const MermaidCodeBlock = CodeBlockLowlight.extend({
  addAttributes() {
    return {
      ...this.parent?.(),
      width: {
        default: null,
        parseHTML: el => el.getAttribute('data-width') || null,
        renderHTML: attrs => attrs.width ? { 'data-width': attrs.width } : {}
      }
    }
  },
  addNodeView() {
    return props => mermaidNodeView(props)
  }
})

function mermaidNodeView({ node, editor, getPos }) {
  const dom = document.createElement('div')
  dom.className = 'cb-node'
  const pre = document.createElement('pre')
  const code = document.createElement('code')
  if (node.attrs.language) code.className = `language-${node.attrs.language}`
  pre.appendChild(code)

  // Non-mermaid code block: same shape as the default so lowlight decorations still apply.
  // Recreate the view (return false) if the language later becomes mermaid.
  if (node.attrs.language !== 'mermaid') {
    dom.appendChild(pre)
    return { dom, contentDOM: code, update: n => n.type === node.type && n.attrs.language !== 'mermaid' }
  }

  dom.classList.add('mermaid-node')
  if (node.attrs.width) dom.style.width = node.attrs.width + 'px'

  const preview = document.createElement('div')
  preview.className = 'mermaid-preview'

  const bar = document.createElement('div')
  bar.className = 'mermaid-bar'
  const toggle = document.createElement('button')
  toggle.type = 'button'
  toggle.textContent = 'Edit code'
  bar.appendChild(toggle)

  const handle = document.createElement('div')
  handle.className = 'mermaid-resize'

  pre.style.display = 'none'
  dom.append(bar, preview, pre, handle)

  let editing = false
  function setMode(edit) {
    editing = edit
    pre.style.display = edit ? '' : 'none'
    preview.style.display = edit ? 'none' : ''
    toggle.textContent = edit ? 'Preview' : 'Edit code'
    if (edit) editor.commands.focus()
    else renderPreview()
  }
  toggle.addEventListener('mousedown', e => { e.preventDefault(); setMode(!editing) })
  preview.addEventListener('click', () => { if (!editing) setMode(true) })

  async function renderPreview() {
    const src = node.textContent.trim()
    if (!src) { preview.innerHTML = '<div class="mermaid-msg">Empty diagram — click to edit.</div>'; return }
    try {
      const m = await loadMermaid()
      m.initialize({ startOnLoad: false, securityLevel: 'strict', theme: 'default' })
      const { svg } = await m.render('mmd-' + Math.random().toString(36).slice(2), src)
      preview.innerHTML = svg
    } catch (err) {
      preview.innerHTML = '<div class="mermaid-msg mermaid-err"></div>'
      preview.firstChild.textContent = (err && err.message) ? err.message : 'Invalid Mermaid syntax.'
    }
  }

  handle.addEventListener('pointerdown', e => {
    e.preventDefault()
    const startX = e.clientX
    const startW = dom.offsetWidth
    const move = ev => { dom.style.width = Math.max(160, startW + ev.clientX - startX) + 'px' }
    const up = ev => {
      document.removeEventListener('pointermove', move)
      document.removeEventListener('pointerup', up)
      const width = Math.max(160, startW + ev.clientX - startX)
      if (typeof getPos === 'function')
        editor.commands.command(({ tr }) => { tr.setNodeMarkup(getPos(), undefined, { ...node.attrs, width }); return true })
    }
    document.addEventListener('pointermove', move)
    document.addEventListener('pointerup', up)
  })

  renderPreview()

  return {
    dom,
    contentDOM: code,
    ignoreMutation: m => m.type !== 'selection' && !code.contains(m.target),
    update(updated) {
      if (updated.type !== node.type) return false
      if ((updated.attrs.language === 'mermaid') !== (node.attrs.language === 'mermaid')) return false
      node = updated
      if (updated.attrs.width) dom.style.width = updated.attrs.width + 'px'
      if (!editing) renderPreview()
      return true
    }
  }
}

// Grab element refs up front: the BubbleMenu extension detaches #bubble from the
// document on mount, so getElementById('bubble') would return null afterwards.
const editorEl = document.getElementById('editor')
const noteMeta = document.getElementById('note-meta')
const bubble = document.getElementById('bubble')

const editor = new Editor({
  element: editorEl,
  editorProps: {
    attributes: {
      spellcheck: 'false',
      autocorrect: 'off',
      autocapitalize: 'off'
    }
  },
  extensions: [
    StarterKit.configure({ codeBlock: false }),
    Markdown.configure({ html: false, tightLists: true, transformPastedText: true }),
    MermaidCodeBlock.configure({ lowlight, defaultLanguage: null }),
    Underline,
    Highlight,
    Link.configure({ openOnClick: true, autolink: true }),
    TaskList,
    TaskItem.configure({ nested: true }),
    TextStyle,
    FontSize,
    LineHeight,
    ResizableImage.configure({ inline: false, allowBase64: true }),
    Table.configure({ resizable: true }),
    TableRow,
    TableHeader,
    TableCell,
    Placeholder.configure({ placeholder: 'Start writing…' }),
    BubbleMenu.configure({ element: document.getElementById('bubble') })
  ],
  content: '',
  autofocus: false,
  onUpdate: () => {
    if (loading) return
    scheduleSave()
    updateSlashMenu()
    renderMeta()
  },
  onSelectionUpdate: () => { syncBubble(); updateSlashMenu(); syncTypography(); updateTableTools() }
})

// --- save (debounced) ---
function scheduleSave() {
  if (saveTimer) clearTimeout(saveTimer)
  saveTimer = setTimeout(pushSave, 400)
}

function pushSave() {
  const text = editor.getText()
  const title = (text.split('\n').find(l => l.trim().length > 0) || '').trim()
  host?.postMessage({ type: 'save', title, md: editor.storage.markdown.getMarkdown(), text })
}

// --- formatting commands (shared by toolbar + bubble) ---
const toolbar = document.getElementById('toolbar')

function runCommand(cmd) {
  const c = editor.chain().focus()
  switch (cmd) {
    case 'bold': c.toggleBold().run(); break
    case 'italic': c.toggleItalic().run(); break
    case 'underline': c.toggleUnderline().run(); break
    case 'strike': c.toggleStrike().run(); break
    case 'highlight': c.toggleHighlight().run(); break
    case 'h1': c.toggleHeading({ level: 1 }).run(); break
    case 'h2': c.toggleHeading({ level: 2 }).run(); break
    case 'bullet': c.toggleBulletList().run(); break
    case 'ordered': c.toggleOrderedList().run(); break
    case 'task': c.toggleTaskList().run(); break
    case 'quote': c.toggleBlockquote().run(); break
    case 'code': c.toggleCodeBlock().run(); break
    case 'meeting-note': runMeetingNoteCommand(); break
    case 'enhance': runEnhanceCommand(); break
    case 'font': document.body.classList.toggle('serif'); break
    case 'ruled': document.body.classList.toggle('ruled'); break
    case 'table': c.insertTable({ rows: 3, cols: 3, withHeaderRow: true }).run(); break
    case 'divider': c.setHorizontalRule().run(); break
    case 'undo': c.undo().run(); break
    case 'redo': c.redo().run(); break
    case 'link': setLink(); break
    case 'format': toggleFormatMenu(); return   // not an editor command; skip syncActive
  }
  syncActive()
}

// Add/edit/remove a link on the current selection. ponytail: window.prompt is enough
// here — WebView2 allows it and it's a user-driven, one-off action.
function setLink() {
  const prev = editor.getAttributes('link').href || ''
  const url = window.prompt('Link URL', prev)
  if (url === null) return
  if (url === '') editor.chain().focus().extendMarkRange('link').unsetLink().run()
  else editor.chain().focus().extendMarkRange('link').setLink({ href: url }).run()
}

// --- Format (⋮) overflow menu ---
const formatMenu = document.getElementById('format-menu')
const btnFormat = document.getElementById('btn-format')

// Move the menu out of #toolbar: the toolbar's backdrop-filter creates a stacking context
// that trapped the dropdown behind the note. As a body child it layers above the editor.
document.body.appendChild(formatMenu)

function toggleFormatMenu() {
  if (formatMenu.hidden) {
    const r = btnFormat.getBoundingClientRect()
    formatMenu.style.left = `${Math.min(r.left, window.innerWidth - 220)}px`
    formatMenu.style.top = `${r.bottom + 4}px`
    formatMenu.hidden = false
  } else {
    formatMenu.hidden = true
  }
}
function hideFormatMenu() { if (!formatMenu.hidden) formatMenu.hidden = true }

// Close on any outside click; selecting an item closes it via wire() below.
document.addEventListener('mousedown', e => {
  if (formatMenu.hidden) return
  if (!formatMenu.contains(e.target) && e.target !== btnFormat) hideFormatMenu()
}, true)

// --- font size + line height dropdowns (Docs-style) ---
const selFont = document.getElementById('sel-fontsize')
const selLine = document.getElementById('sel-lineheight')

selFont.addEventListener('change', () => {
  const v = selFont.value
  if (v) editor.chain().focus().setFontSize(v).run()
  else editor.chain().focus().unsetFontSize().run()
})

selLine.addEventListener('change', () => {
  editor.chain().focus().setLineHeight(selLine.value || null).run()
})

// Reflect the current selection's size + line height back into the dropdowns.
function syncTypography() {
  selFont.value = editor.getAttributes('textStyle').fontSize || ''
  selLine.value = editor.getAttributes('paragraph').lineHeight
    || editor.getAttributes('heading').lineHeight || ''
}

function wire(container) {
  container.querySelectorAll('button').forEach(btn => {
    btn.addEventListener('mousedown', e => {
      e.preventDefault()
      runCommand(btn.dataset.cmd)
      if (btn.closest('#format-menu')) hideFormatMenu()
    })
  })
}
wire(toolbar)
wire(bubble)
wire(formatMenu)   // now a body child, wire its buttons directly

const ACTIVE = {
  bold: 'bold', italic: 'italic', underline: 'underline', strike: 'strike', highlight: 'highlight',
  h1: () => editor.isActive('heading', { level: 1 }),
  h2: () => editor.isActive('heading', { level: 2 }),
  bullet: 'bulletList', ordered: 'orderedList', task: 'taskList', quote: 'blockquote', code: 'codeBlock'
}

function syncActive() {
  for (const container of [toolbar, bubble, formatMenu]) {
    container.querySelectorAll('button').forEach(btn => {
      const key = ACTIVE[btn.dataset.cmd]
      if (key === undefined) return
      const active = typeof key === 'function' ? key() : editor.isActive(key)
      btn.classList.toggle('active', !!active)
    })
  }
}
const syncBubble = syncActive

// --- slash commands (/ at start of a line or after a space) ---
const SLASH_ITEMS = [
  { label: 'Templates', keys: 'template templates start new gallery pick', run: () => openTemplatePicker() },
  { label: 'Project tracker', keys: 'tracker project tasks roadmap status', run: c => c.clearNodes().insertContent(trackerTemplate()) },
  { label: 'Meeting note', ai: true, keys: 'meeting-note meeting notes teams call transcript vtt agenda decisions actions', run: () => runMeetingNoteCommand() },
  { label: 'Enhance writing', ai: true, keys: 'enhance rewrite polish grammar improve fix email message clean send draft', run: () => runEnhanceCommand() },
  { label: 'Heading 1',     keys: 'h1 heading title big',      run: c => c.toggleHeading({ level: 1 }) },
  { label: 'Heading 2',     keys: 'h2 heading subtitle',       run: c => c.toggleHeading({ level: 2 }) },
  { label: 'Heading 3',     keys: 'h3 heading',                run: c => c.toggleHeading({ level: 3 }) },
  { label: 'Bullet list',   keys: 'bullet unordered list ul',  run: c => c.toggleBulletList() },
  { label: 'Numbered list', keys: 'numbered ordered list ol',  run: c => c.toggleOrderedList() },
  { label: 'To-do list',    keys: 'todo task checkbox check',  run: c => c.toggleTaskList() },
  { label: 'Quote',         keys: 'quote blockquote',          run: c => c.toggleBlockquote() },
  { label: 'Code block',    keys: 'code pre snippet',          run: c => c.toggleCodeBlock() },
  { label: 'Mermaid diagram', keys: 'mermaid diagram flowchart chart graph sequence gantt', run: c => c.insertContent({ type: 'codeBlock', attrs: { language: 'mermaid' }, content: [{ type: 'text', text: 'graph TD\n  A[Start] --> B[Next]\n  B --> C[Done]' }] }) },
  { label: 'Table',         keys: 'table grid rows columns',   run: c => c.insertTable({ rows: 3, cols: 3, withHeaderRow: true }) },
  { label: 'Divider',       keys: 'divider hr horizontal rule line', run: c => c.setHorizontalRule() }
]

function trackerTemplate() {
  const p = text => ({ type: 'paragraph', content: text ? [{ type: 'text', text }] : undefined })
  const bold = text => ({ type: 'paragraph', content: [{ type: 'text', text, marks: [{ type: 'bold' }] }] })
  const h = (level, text) => ({ type: 'heading', attrs: { level }, content: [{ type: 'text', text }] })
  const task = text => ({
    type: 'taskItem',
    attrs: { checked: false },
    content: [p(text)]
  })
  const cell = text => ({ type: 'tableCell', content: [p(text)] })
  const row = (...cells) => ({ type: 'tableRow', content: cells.map(cell) })
  const table = rows => ({ type: 'table', content: rows.map(r => row(...r)) })

  return [
    h(1, 'Project Tracker'),
    bold('Goal'),
    p('Build a clear, shippable plan.'),
    { type: 'horizontalRule' },
    h(2, 'Today'),
    { type: 'taskList', content: [
      task('Review current progress'),
      task('Update risk log'),
      task('Send summary to team')
    ] },
    { type: 'horizontalRule' },
    h(2, 'This Week'),
    table([
      ['Complete core module', 'Working through implementation.', '01 Jul'],
      ['Write tests', 'Waiting for final flow.', '02 Jul'],
      ['Prepare demo', 'Needs review recording.', '03 Jul']
    ]),
    { type: 'horizontalRule' },
    h(2, 'Waiting / Blocked'),
    table([
      ['Security review', 'Waiting for updated guidelines.', 'ETA: 02 Jul'],
      ['Staging environment', 'Environment not ready.', 'ETA: 03 Jul']
    ]),
    { type: 'horizontalRule' },
    h(2, 'Notes / Comments'),
    { type: 'bulletList', content: [{ type: 'listItem', content: [p('Key decisions and follow-ups go here.')] }] }
  ]
}

// --- start-from-template: blank structured notes the user picks to begin a note ---
function meetingTemplate() {
  const p = text => ({ type: 'paragraph', content: text ? [{ type: 'text', text }] : undefined })
  const h = (level, text) => ({ type: 'heading', attrs: { level }, content: [{ type: 'text', text }] })
  const task = text => ({ type: 'taskItem', attrs: { checked: false }, content: [p(text)] })
  const cell = text => ({ type: 'tableCell', content: [p(text)] })
  const row = (...cells) => ({ type: 'tableRow', content: cells.map(cell) })
  const table = rows => ({ type: 'table', content: rows.map(r => row(...r)) })
  const today = new Date().toLocaleDateString(undefined, { day: 'numeric', month: 'long', year: 'numeric' })

  return [
    h(1, 'Meeting Notes'),
    p(today),
    { type: 'horizontalRule' },
    h(2, 'Agenda'),
    { type: 'bulletList', content: [{ type: 'listItem', content: [p('Review progress and open items.')] }] },
    { type: 'horizontalRule' },
    h(2, 'Decisions'),
    { type: 'bulletList', content: [{ type: 'listItem', content: [p('Decision goes here.')] }] },
    { type: 'horizontalRule' },
    h(2, 'Action Items'),
    { type: 'taskList', content: [
      task('Follow up with owner'),
      task('Update project tracker'),
      task('Share meeting summary')
    ] },
    { type: 'horizontalRule' },
    h(2, 'Open Questions'),
    table([
      ['Question', 'Owner', 'Due'],
      ['What still needs review?', 'Unassigned', 'TBD']
    ]),
    { type: 'horizontalRule' },
    h(2, 'Notes'),
    { type: 'bulletList', content: [{ type: 'listItem', content: [p('Key context and discussion points go here.')] }] }
  ]
}

function standupTemplate() {
  const p = text => ({ type: 'paragraph', content: text ? [{ type: 'text', text }] : undefined })
  const h = (level, text) => ({ type: 'heading', attrs: { level }, content: [{ type: 'text', text }] })
  const task = text => ({ type: 'taskItem', attrs: { checked: false }, content: [p(text)] })
  const today = new Date().toLocaleDateString(undefined, { day: 'numeric', month: 'long', year: 'numeric' })

  return [
    h(1, 'Daily Standup'),
    p(today),
    { type: 'horizontalRule' },
    h(2, 'Yesterday'),
    { type: 'bulletList', content: [{ type: 'listItem', content: [p('What I finished.')] }] },
    h(2, 'Today'),
    { type: 'taskList', content: [task('What I plan to do')] },
    h(2, 'Blockers'),
    { type: 'bulletList', content: [{ type: 'listItem', content: [p('Anything in my way (or “None”).')] }] }
  ]
}

function oneOnOneTemplate() {
  const p = text => ({ type: 'paragraph', content: text ? [{ type: 'text', text }] : undefined })
  const h = (level, text) => ({ type: 'heading', attrs: { level }, content: [{ type: 'text', text }] })
  const task = text => ({ type: 'taskItem', attrs: { checked: false }, content: [p(text)] })
  const today = new Date().toLocaleDateString(undefined, { day: 'numeric', month: 'long', year: 'numeric' })

  return [
    h(1, '1:1 Notes'),
    p(today),
    { type: 'horizontalRule' },
    h(2, 'Talking Points'),
    { type: 'bulletList', content: [{ type: 'listItem', content: [p('What I want to cover.')] }] },
    h(2, 'Feedback'),
    { type: 'bulletList', content: [{ type: 'listItem', content: [p('Given and received.')] }] },
    h(2, 'Action Items'),
    { type: 'taskList', content: [task('Follow-up with owner and due date')] }
  ]
}

function weeklyStatusTemplate() {
  const p = text => ({ type: 'paragraph', content: text ? [{ type: 'text', text }] : undefined })
  const h = (level, text) => ({ type: 'heading', attrs: { level }, content: [{ type: 'text', text }] })
  const task = text => ({ type: 'taskItem', attrs: { checked: false }, content: [p(text)] })
  const bullet = text => ({ type: 'bulletList', content: [{ type: 'listItem', content: [p(text)] }] })
  const today = new Date().toLocaleDateString(undefined, { day: 'numeric', month: 'long', year: 'numeric' })

  return [
    h(1, 'Weekly Status Update'),
    p(`Week of ${today}`),
    { type: 'horizontalRule' },
    h(2, 'Accomplishments'),
    bullet('What shipped or moved forward this week.'),
    h(2, 'In Progress'),
    bullet('What is underway and its status.'),
    h(2, 'Blockers'),
    bullet('What is stuck and what would unblock it (or “None”).'),
    h(2, 'Next Week'),
    { type: 'taskList', content: [task('Top priority for next week')] },
    h(2, 'Asks / Needs'),
    bullet('Decisions or help needed from others.')
  ]
}

function projectBriefTemplate() {
  const p = text => ({ type: 'paragraph', content: text ? [{ type: 'text', text }] : undefined })
  const h = (level, text) => ({ type: 'heading', attrs: { level }, content: [{ type: 'text', text }] })
  const bullet = text => ({ type: 'bulletList', content: [{ type: 'listItem', content: [p(text)] }] })
  const cell = text => ({ type: 'tableCell', content: [p(text)] })
  const row = (...cells) => ({ type: 'tableRow', content: cells.map(cell) })
  const table = rows => ({ type: 'table', content: rows.map(r => row(...r)) })

  return [
    h(1, 'Project Brief'),
    { type: 'horizontalRule' },
    h(2, 'Objective'),
    p('The problem this project solves and the outcome we want.'),
    h(2, 'Scope'),
    table([
      ['In Scope', 'Out of Scope'],
      ['What we will do', 'What we explicitly will not do']
    ]),
    h(2, 'Stakeholders'),
    table([
      ['Name', 'Role'],
      ['Unassigned', 'Owner / Sponsor / Contributor']
    ]),
    h(2, 'Milestones'),
    table([
      ['Milestone', 'Target date'],
      ['Kickoff', 'TBD']
    ]),
    h(2, 'Success Metrics'),
    bullet('How we know this project succeeded.'),
    h(2, 'Risks'),
    table([
      ['Risk', 'Mitigation'],
      ['What could go wrong', 'How we reduce it']
    ])
  ]
}

const TEMPLATES = [
  { name: 'Meeting Notes',        icon: '📝', desc: 'Agenda, decisions, action items, open questions',   build: meetingTemplate },
  { name: 'Project Tracker',      icon: '✅', desc: 'Goal, tasks, weekly plan, blockers',                build: trackerTemplate },
  { name: 'Weekly Status Update', icon: '📊', desc: 'Accomplishments, in progress, blockers, next week', build: weeklyStatusTemplate },
  { name: 'Project Brief',        icon: '📋', desc: 'Objective, scope, stakeholders, milestones, risks', build: projectBriefTemplate },
  { name: 'Daily Standup',        icon: '☀',  desc: 'Yesterday, today, blockers',                        build: standupTemplate },
  { name: '1:1 Notes',            icon: '🤝', desc: 'Talking points, feedback, action items',             build: oneOnOneTemplate }
]

// Template picker overlay: a small modal listing templates. Picking one replaces the
// note's content with that structure; "Blank note" just closes and leaves it empty.
const templatePicker = document.createElement('div')
templatePicker.id = 'template-picker'
templatePicker.hidden = true
document.body.appendChild(templatePicker)

function openTemplatePicker() {
  templatePicker.innerHTML = `
    <div class="tp-backdrop"></div>
    <div class="tp-card" role="dialog" aria-label="Start from a template">
      <div class="tp-title">Start from a template</div>
      <div class="tp-list">
        ${TEMPLATES.map((t, i) => `
          <button class="tp-item" data-i="${i}">
            <span class="tp-icon">${t.icon}</span>
            <span class="tp-text"><span class="tp-name">${t.name}</span><span class="tp-desc">${t.desc}</span></span>
          </button>`).join('')}
        <button class="tp-item tp-blank" data-i="-1">
          <span class="tp-icon">—</span>
          <span class="tp-text"><span class="tp-name">Blank note</span><span class="tp-desc">Start with an empty page</span></span>
        </button>
      </div>
    </div>`
  templatePicker.hidden = false

  templatePicker.querySelectorAll('.tp-item').forEach(btn =>
    btn.addEventListener('click', () => applyTemplate(Number(btn.dataset.i))))
  templatePicker.querySelector('.tp-backdrop').addEventListener('click', closeTemplatePicker)
}

function closeTemplatePicker() {
  templatePicker.hidden = true
  editor.commands.focus('end')
}

function applyTemplate(index) {
  closeTemplatePicker()
  if (index < 0) return   // Blank note
  editor.chain().focus().clearContent().insertContent(TEMPLATES[index].build()).run()
  pushSave()
}

function meetingNotesFromText(rawText) {
  const p = text => ({ type: 'paragraph', content: text ? [{ type: 'text', text }] : undefined })
  const h = (level, text) => ({ type: 'heading', attrs: { level }, content: [{ type: 'text', text }] })
  const task = text => ({ type: 'taskItem', attrs: { checked: false }, content: [p(text)] })
  const list = items => ({ type: 'bulletList', content: items.map(text => ({ type: 'listItem', content: [p(text)] })) })
  const cell = text => ({ type: 'tableCell', content: [p(text)] })
  const row = (...cells) => ({ type: 'tableRow', content: cells.map(cell) })
  const table = rows => ({ type: 'table', content: rows.map(r => row(...r)) })

  const cleaned = normalizeTranscript(rawText)
  const today = new Date().toLocaleDateString(undefined, { day: 'numeric', month: 'long', year: 'numeric' })
  const decisions = unique(cleaned.lines.filter(isDecision).map(cleanSentence)).slice(0, 8)
  const actions = unique(cleaned.lines.filter(isAction).map(cleanAction)).slice(0, 10)
  const blockers = unique(cleaned.lines.filter(isBlocker).map(cleanSentence)).slice(0, 6)
  const discussion = unique(cleaned.lines.filter(line =>
    !isDecision(line) && !isAction(line) && !isBlocker(line) && line.length > 18
  ).map(cleanSentence)).slice(0, 8)

  return [
    h(1, cleaned.title || 'Meeting Notes'),
    p(today),
    cleaned.attendees.length ? p(`Attendees: ${cleaned.attendees.join(', ')}`) : p('Attendees:'),
    { type: 'horizontalRule' },
    h(2, 'Summary'),
    list(discussion.length ? discussion.slice(0, 4) : ['Paste or type transcript details, then run Clean meeting transcript.']),
    { type: 'horizontalRule' },
    h(2, 'Decisions'),
    list(decisions.length ? decisions : ['No clear decisions detected.']),
    { type: 'horizontalRule' },
    h(2, 'Action Items'),
    { type: 'taskList', content: (actions.length ? actions : ['Review transcript and assign owners.']).map(task) },
    { type: 'horizontalRule' },
    h(2, 'Risks / Blockers'),
    table((blockers.length ? blockers : ['No blockers detected.']).map(item => [item, 'Owner TBD', 'ETA TBD'])),
    { type: 'horizontalRule' },
    h(2, 'Discussion Notes'),
    list(discussion.length ? discussion : ['No additional discussion notes detected.'])
  ]
}

// --- /meeting-note: turn a pasted transcript into clean notes, agentically ---
// Editor owns the flow so the user watches it happen: chunk the transcript, clean each
// block top-to-bottom (each swaps in place when its AI result lands), then one summary
// pass into the final template. C# is just the API caller (clean one chunk / summarise).
const MEETING_CHUNK_CHARS = 4000
let meetingRunning = false

function runMeetingNoteCommand() {
  if (meetingRunning) return
  const raw = editor.getText()
  if (!raw.trim()) {
    setAiStatus('Paste a Teams transcript or notes first, then run /meeting-note.')
    setTimeout(() => setAiStatus(''), 4000)
    return
  }
  meetingRunning = true
  cleanMeeting(raw).finally(() => {
    meetingRunning = false
    editorEl.classList.remove('ai-busy')
  })
}

async function cleanMeeting(raw) {
  editorEl.classList.add('ai-busy')
  setAiStatus('Checking OpenAI key…')

  const status = await callHost('ai-ensure-key')
  const mode = status?.mode || 'cancel'

  if (mode === 'cancel') {
    setAiStatus('')
    return
  }

  if (mode === 'offline') {
    setAiStatus('Cleaning transcript offline (lower quality)…')
    replaceWithNodes(meetingNotesFromText(raw))
    finishMeeting('Meeting notes ready (offline).')
    return
  }

  // mode === 'key' — agentic loop over chunks
  const blocks = chunkTranscript(extractTranscriptText(raw), MEETING_CHUNK_CHARS)
    .map(text => ({ raw: text, cleaned: null, status: 'pending' }))

  if (blocks.length === 0) { setAiStatus(''); return }

  window.scrollTo(0, 0)
  renderMeetingProgress(blocks, -1)

  for (let i = 0; i < blocks.length; i++) {
    blocks[i].status = 'working'
    setAiStatus(`PaperNote is cleaning section ${i + 1} of ${blocks.length}…`)
    renderMeetingProgress(blocks, i)

    const res = await callHost('ai-chunk', { index: i, text: blocks[i].raw })
    blocks[i].cleaned = (res?.ok && res.markdown) ? res.markdown : blocks[i].raw
    blocks[i].status = 'done'
    renderMeetingProgress(blocks, -1)
  }

  setAiStatus('Writing the minutes…')
  // The full clean script is the source of truth — the editor keeps it. The final pass only
  // writes the header (TL;DR / decisions / actions) that sits above it, so nothing is lost.
  const fullScript = blocks.map(b => b.cleaned).join('\n\n')
  const finalRes = await callHost('ai-final', { text: fullScript })
  const header = (finalRes?.ok && finalRes.markdown) ? finalRes.markdown.trim() : ''
  const doc = header
    ? `${header}\n\n---\n\n## Full Transcript\n\n${fullScript}`
    : fullScript
  replaceWithMarkdown(doc)

  finishMeeting('Meeting notes ready.')
  if (finalRes && !finalRes.ok && finalRes.error)
    window.alert(`Minutes header failed:\n\n${finalRes.error}\n\nKept the full clean transcript.`)
}

function finishMeeting(message) {
  setAiStatus(message)
  setTimeout(() => setAiStatus(''), 3000)
  pushSave()
}

// --- /enhance: rewrite pasted text (email / message / notes) into a clean, send-ready
// version. Single AI pass — the input is short, so no chunking. Replaces the note; Ctrl+Z
// brings the original back. ---
let enhancing = false

function runEnhanceCommand() {
  if (enhancing) return
  const raw = editor.getText()
  if (!raw.trim()) {
    setAiStatus('Paste an email, message, or notes first, then run /enhance.')
    setTimeout(() => setAiStatus(''), 4000)
    return
  }
  enhancing = true
  enhanceText(raw).finally(() => {
    enhancing = false
    editorEl.classList.remove('ai-busy')
  })
}

async function enhanceText(raw) {
  editorEl.classList.add('ai-busy')
  setAiStatus('Checking OpenAI key…')

  const status = await callHost('ai-ensure-key')
  const mode = status?.mode || 'cancel'
  if (mode !== 'key') {
    if (mode === 'offline') {
      setAiStatus('Rewrite needs an OpenAI key.')
      setTimeout(() => setAiStatus(''), 3500)
    } else {
      setAiStatus('')
    }
    return
  }

  setAiStatus('Rewriting…')
  const res = await callHost('ai-enhance', { text: raw })
  if (res?.ok && res.markdown) {
    replaceWithMarkdown(res.markdown.trim())
    finishMeeting('Rewritten and ready to send.')
    return
  }

  setAiStatus('')
  if (res?.error) window.alert(`Rewrite failed:\n\n${res.error}`)
}

// Split transcript into blocks near maxChars, breaking on a line boundary so we never
// cut a sentence mid-line (transcripts are line-based: VTT cues, Teams speaker turns).
function chunkTranscript(text, maxChars) {
  const norm = text.replace(/\r\n/g, '\n')
  const out = []
  for (let start = 0; start < norm.length;) {
    let end = Math.min(start + maxChars, norm.length)
    if (end < norm.length) {
      const nl = norm.lastIndexOf('\n', end)
      if (nl > start + maxChars / 2) end = nl
    }
    out.push(norm.slice(start, end).trim())
    start = end
  }
  return out.filter(Boolean)
}

// Re-render the whole doc from block state each step: done blocks show their cleaned
// Markdown, the working block gets a live banner above it, pending blocks stay raw.
function renderMeetingProgress(blocks, workingIndex) {
  const total = blocks.length
  const parts = []
  blocks.forEach((b, i) => {
    if (b.status === 'done') { parts.push(b.cleaned); return }
    if (i === workingIndex) parts.push(`> ⏳ **PaperNote is cleaning section ${i + 1} of ${total}…**`)
    parts.push(b.raw)
  })
  replaceWithMarkdown(parts.join('\n\n'))

  if (workingIndex >= 0) requestAnimationFrame(() => {
    const marker = [...editorEl.querySelectorAll('blockquote')]
      .find(el => el.textContent.includes('PaperNote is cleaning'))
    marker?.scrollIntoView({ block: 'center', behavior: 'smooth' })
  })
}

function replaceWithMarkdown(md) {
  loading = true
  editor.commands.setContent(md, false)
  loading = false
}

function replaceWithNodes(nodes) {
  loading = true
  editor.chain().clearContent().insertContent(nodes).run()
  loading = false
}

function extractTranscriptText(text) {
  return text
    .replace(/\r/g, '\n')
    .replace(/^WEBVTT[\s\S]*?(?=\n\n|\n\d{1,})/i, '')
    .split('\n')
    .map(line => line.trim())
    .filter(line => line && !/^\d+$/.test(line))
    .filter(line => !/^\d{2}:\d{2}:\d{2}\.\d{3}\s+-->\s+\d{2}:\d{2}:\d{2}\.\d{3}/.test(line))
    .filter(line => !/^NOTE\b|^STYLE\b|^REGION\b/i.test(line))
    .join('\n')
}

function normalizeTranscript(rawText) {
  const attendees = new Set()
  const lines = rawText
    .replace(/\r/g, '\n')
    .split('\n')
    .map(line => line.trim())
    .filter(Boolean)
    .map(line => line
      .replace(/^\/\w+\s*/g, '')
      .replace(/^\[?\d{1,2}:\d{2}(?::\d{2})?\]?\s*/g, '')
      .replace(/^\d{1,2}:\d{2}\s?(AM|PM)?\s*/i, '')
      .trim())
    .filter(line => line.length > 0)
    .map(line => {
      const speaker = line.match(/^([A-Z][A-Za-z .'-]{1,32}):\s+(.+)$/)
      if (!speaker) return line
      attendees.add(speaker[1].trim())
      return speaker[2].trim()
    })
    .filter(line => line.length > 0)

  const titleLine = lines.find(line => /meeting|sync|standup|review|planning|retro/i.test(line))
  return { title: titleLine ? titleCase(titleLine.replace(/^(title|subject):\s*/i, '')) : 'Meeting Notes', attendees: [...attendees], lines }
}

function isDecision(line) {
  return /\b(decided|decision|agreed|approved|confirmed|we will|we'll|finalized|sign.?off)\b/i.test(line)
}

function isAction(line) {
  return /\b(action|todo|to do|follow up|follow-up|please|need to|needs to|will|assign|owner|by \w+day|eta|due)\b/i.test(line)
}

function isBlocker(line) {
  return /\b(blocked|blocker|risk|issue|waiting|pending|dependency|depends|not ready|concern|problem)\b/i.test(line)
}

function cleanSentence(line) {
  return line.replace(/^(decision|action|todo|note|summary)\s*[:\-]\s*/i, '').replace(/\s+/g, ' ').trim()
}

function cleanAction(line) {
  return cleanSentence(line).replace(/^(we need to|need to|please|action item)\s+/i, '')
}

function unique(items) {
  const seen = new Set()
  return items.filter(item => {
    const key = item.toLowerCase()
    if (seen.has(key)) return false
    seen.add(key)
    return item.length > 0
  })
}

function titleCase(text) {
  const trimmed = cleanSentence(text)
  if (trimmed.length > 80) return 'Meeting Notes'
  return trimmed.replace(/\w\S*/g, word => word[0].toUpperCase() + word.slice(1))
}

const slashMenu = document.createElement('div')
slashMenu.id = 'slash-menu'
slashMenu.style.display = 'none'
document.body.appendChild(slashMenu)

let slashOpen = false
let slashItems = []
let slashIndex = 0

// What '/query' (if any) sits right before the caret? Recomputed every edit — stateless.
function slashContext() {
  const { state } = editor
  const { $from, empty } = state.selection
  if (!empty) return null
  const textBefore = $from.parent.textBetween(0, $from.parentOffset, '\n', '\0')
  const m = textBefore.match(/(?:^|\s)\/(\w*)$/)
  if (!m) return null
  return { query: m[1].toLowerCase(), from: $from.pos - m[1].length - 1 }
}

function updateSlashMenu() {
  const ctx = slashContext()
  if (!ctx) return hideSlashMenu()

  slashItems = SLASH_ITEMS.filter(i =>
    !ctx.query || (i.label + ' ' + i.keys).toLowerCase().split(/\s+/).some(w => w.startsWith(ctx.query)))
  if (slashItems.length === 0) return hideSlashMenu()

  slashIndex = 0
  renderSlashMenu()

  const coords = editor.view.coordsAtPos(editor.state.selection.from)
  slashMenu.style.left = `${coords.left}px`
  slashMenu.style.top = `${coords.bottom + 4}px`
  slashMenu.style.display = 'block'
  slashOpen = true
}

function renderSlashMenu() {
  slashMenu.innerHTML = ''
  slashItems.forEach((item, i) => {
    const el = document.createElement('div')
    el.className = 'slash-item' + (i === slashIndex ? ' active' : '')
    el.textContent = item.label
    if (item.ai) {
      const badge = document.createElement('sup')
      badge.className = 'ai-badge'
      badge.textContent = 'AI'
      el.appendChild(badge)
    }
    el.addEventListener('mousedown', e => { e.preventDefault(); selectSlashItem(i) })
    slashMenu.appendChild(el)
  })
}

function hideSlashMenu() {
  if (!slashOpen) return
  slashMenu.style.display = 'none'
  slashOpen = false
}

function selectSlashItem(i) {
  const ctx = slashContext()
  const item = slashItems[i]
  if (!ctx || !item) return hideSlashMenu()
  const chain = editor.chain().focus().deleteRange({ from: ctx.from, to: editor.state.selection.from })
  const result = item.run(chain)
  // Insert-type items return the chain to run; action-type items (Templates, Meeting note)
  // return nothing — run the delete chain ourselves so the "/query" text still goes away.
  if (result && typeof result.run === 'function') result.run()
  else chain.run()
  hideSlashMenu()
}

// Capture arrows/enter/escape before ProseMirror while the menu is open.
editorEl.addEventListener('keydown', e => {
  if (!slashOpen) return
  switch (e.key) {
    case 'ArrowDown': slashIndex = (slashIndex + 1) % slashItems.length; renderSlashMenu(); e.preventDefault(); break
    case 'ArrowUp':   slashIndex = (slashIndex - 1 + slashItems.length) % slashItems.length; renderSlashMenu(); e.preventDefault(); break
    case 'Enter':     selectSlashItem(slashIndex); e.preventDefault(); break
    case 'Escape':    hideSlashMenu(); e.preventDefault(); break
  }
}, true)

// --- table tools: a small floating bar shown only when the cursor is in a table ---
const tableTools = document.createElement('div')
tableTools.id = 'table-tools'
tableTools.style.display = 'none'
tableTools.innerHTML = `
  <button data-t="addColumnAfter" title="Add column">+ Col</button>
  <button data-t="deleteColumn" title="Delete column">– Col</button>
  <span class="tt-sep"></span>
  <button data-t="addRowAfter" title="Add row">+ Row</button>
  <button data-t="deleteRow" title="Delete row">– Row</button>
  <span class="tt-sep"></span>
  <button data-t="toggleHeaderRow" title="Toggle header row">Header</button>
  <span class="tt-sep"></span>
  <button data-fn="math" title="Column totals (sum, average, count, min, max)">&#931; Sum</button>
  <button data-fn="fill" title="Fill the column down as a number or date series">Fill &#8595;</button>
  <span class="tt-sep"></span>
  <button data-t="deleteTable" title="Delete table" class="tt-danger">Delete</button>`
document.body.appendChild(tableTools)

tableTools.querySelectorAll('button[data-t]').forEach(btn =>
  btn.addEventListener('mousedown', e => {
    e.preventDefault()
    editor.chain().focus()[btn.dataset.t]().run()
    updateTableTools()
  }))

tableTools.querySelector('[data-fn="math"]').addEventListener('mousedown', e => { e.preventDefault(); showColumnMath() })
tableTools.querySelector('[data-fn="fill"]').addEventListener('mousedown', e => { e.preventDefault(); fillDownColumn() })

function updateTableTools() {
  if (!editor.isActive('table')) { tableTools.style.display = 'none'; return }
  const dom = editor.view.domAtPos(editor.state.selection.from)?.node
  const tableEl = (dom.nodeType === 1 ? dom : dom.parentElement)?.closest('table')
  if (!tableEl) { tableTools.style.display = 'none'; return }
  const r = tableEl.getBoundingClientRect()
  tableTools.style.display = 'flex'
  tableTools.style.left = `${r.left}px`
  tableTools.style.top = `${Math.max(8, r.top - 38)}px`
}

// ---- table math + autofill (Excel-lite: column totals, insert total row, fill-down series) ----

// The table under the cursor: its node, content-start position, cell map, and the current column.
function tableCtx() {
  const $from = editor.state.selection.$from
  for (let d = $from.depth; d > 0; d--) {
    const node = $from.node(d)
    if (node.type.spec.tableRole === 'table') {
      const start = $from.start(d)
      const map = TableMap.get(node)
      let col = 0
      for (let dd = $from.depth; dd > d; dd--) {
        const n = $from.node(dd)
        if (n.type.spec.tableRole === 'cell' || n.type.spec.tableRole === 'header_cell') {
          col = map.colCount($from.before(dd) - start)
          break
        }
      }
      return { node, start, map, col }
    }
  }
  return null
}

// Body cells of a column, top to bottom (skips header cells and rowspan duplicates).
function columnBodyCells(ctx) {
  const { start, map, col } = ctx
  const out = []
  const seen = new Set()
  for (let row = 0; row < map.height; row++) {
    const rel = map.map[row * map.width + col]
    if (seen.has(rel)) continue
    seen.add(rel)
    const abs = start + rel
    const cell = editor.state.doc.nodeAt(abs)
    if (!cell || cell.type.spec.tableRole === 'header_cell') continue
    out.push({ abs, node: cell, text: cell.textContent.trim() })
  }
  return out
}

function toNum(t) {
  if (!t) return null
  const n = parseFloat(String(t).replace(/[^0-9.\-]/g, ''))
  return Number.isNaN(n) ? null : n
}
const fmtNum = n => Number.isInteger(n) ? String(n) : (Math.round(n * 100) / 100).toString()

function showColumnMath() {
  const ctx = tableCtx()
  if (!ctx) { flash('Put the cursor in a table column.'); return }
  const nums = columnBodyCells(ctx).map(c => toNum(c.text)).filter(n => n !== null)
  if (!nums.length) { flash('No numbers in this column.'); return }
  const sum = nums.reduce((a, b) => a + b, 0)
  openMathPopover(ctx, {
    Sum: fmtNum(sum),
    Average: fmtNum(sum / nums.length),
    Count: String(nums.length),
    Min: fmtNum(Math.min(...nums)),
    Max: fmtNum(Math.max(...nums)),
  }, sum)
}

let mathPop = null
function openMathPopover(ctx, stats, sum) {
  closeMathPopover()
  mathPop = document.createElement('div')
  mathPop.className = 'pn-mathpop'
  mathPop.innerHTML =
    Object.entries(stats).map(([k, v]) =>
      `<div class="pn-mathrow" data-copy="${v}"><span>${k}</span><b>${v}</b></div>`).join('') +
    `<button class="pn-mathadd">Insert total row</button>`
  document.body.appendChild(mathPop)
  const r = tableTools.getBoundingClientRect()
  mathPop.style.left = `${r.left}px`
  mathPop.style.top = `${r.bottom + 6}px`
  mathPop.querySelectorAll('.pn-mathrow').forEach(row =>
    row.addEventListener('mousedown', e => {
      e.preventDefault()
      navigator.clipboard?.writeText(row.dataset.copy)
      flash(`Copied ${row.dataset.copy}`)
    }))
  mathPop.querySelector('.pn-mathadd').addEventListener('mousedown', e => {
    e.preventDefault()
    insertTotalRow(ctx, fmtNum(sum))
    closeMathPopover()
    flash('Total row added.')
  })
}
function closeMathPopover() { if (mathPop) { mathPop.remove(); mathPop = null } }
document.addEventListener('mousedown', e => {
  if (mathPop && !mathPop.contains(e.target) && !tableTools.contains(e.target)) closeMathPopover()
}, true)

function insertTotalRow(ctx, value) {
  const { node, start, map, col } = ctx
  const { schema } = editor.state
  const cells = []
  for (let c = 0; c < map.width; c++) {
    const txt = c === col ? String(value) : ''
    const p = schema.nodes.paragraph.create(null, txt ? schema.text(txt) : null)
    cells.push(schema.nodes.tableCell.create(null, p))
  }
  const rowNode = schema.nodes.tableRow.create(null, cells)
  editor.view.dispatch(editor.state.tr.insert(start + node.content.size, rowNode))
}

// ---- fill-down series ----
const MONTHS = { jan: 0, feb: 1, mar: 2, apr: 3, may: 4, jun: 5, jul: 6, aug: 7, sep: 8, sept: 8, oct: 9, nov: 10, dec: 11 }
const MON_ABBR = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sept', 'Oct', 'Nov', 'Dec']

// Parse "DD Mon YYYY" (Mon incl. "Sept"), remembering how it was written so output matches.
function parseDmy(t) {
  const m = /^(\d{1,2})(\s+)([A-Za-z]+)(\s+)(\d{4})$/.exec(t.trim())
  if (!m) return null
  const mon = MONTHS[m[3].toLowerCase()]
  if (mon == null) return null
  return { date: new Date(+m[5], mon, +m[1]), pad: m[1].length === 2, sep1: m[2], sep2: m[4] }
}
function fmtDmy(date, style) {
  const dd = String(date.getDate())
  const day = style.pad ? dd.padStart(2, '0') : dd
  return `${day}${style.sep1}${MON_ABBR[date.getMonth()]}${style.sep2}${date.getFullYear()}`
}

// Returns i -> value string for the detected series, or null.
function detectSeries(seed) {
  const nums = seed.map(toNum)
  if (nums.every(n => n !== null)) {
    const step = seed.length >= 2 ? nums[1] - nums[0] : 1
    return i => fmtNum(nums[0] + step * i)
  }
  const dts = seed.map(parseDmy)
  if (dts.every(d => d !== null)) {
    const dayMs = 86400000
    const step = dts.length >= 2 ? Math.round((dts[1].date - dts[0].date) / dayMs) : 1
    const style = dts[0]
    return i => fmtDmy(new Date(dts[0].date.getTime() + step * i * dayMs), style)
  }
  return null
}

function fillDownColumn() {
  const ctx = tableCtx()
  if (!ctx) { flash('Put the cursor in a table column.'); return }
  const cells = columnBodyCells(ctx)
  const seed = []
  for (const c of cells) { if (c.text) seed.push(c); else break }
  if (!seed.length) { flash('Type a value in the first cell, then fill down.'); return }
  if (seed.length === cells.length) { flash('No empty cells below to fill.'); return }

  const gen = detectSeries(seed.map(c => c.text))
  if (!gen) { flash("Couldn't detect a number or date series."); return }

  const writes = cells.slice(seed.length).map((c, k) => ({ abs: c.abs, node: c.node, text: gen(seed.length + k) }))
  const { schema } = editor.state
  let tr = editor.state.tr
  writes.sort((a, b) => b.abs - a.abs)   // highest position first so lower positions stay valid
  for (const w of writes) {
    const p = schema.nodes.paragraph.create(null, w.text ? schema.text(String(w.text)) : null)
    tr = tr.replaceWith(w.abs, w.abs + w.node.nodeSize, w.node.type.create(w.node.attrs, p))
  }
  editor.view.dispatch(tr)
  flash(`Filled ${writes.length} cell${writes.length > 1 ? 's' : ''}.`)
}

let flashEl = null
function flash(msg) {
  if (!flashEl) { flashEl = document.createElement('div'); flashEl.id = 'pn-flash'; document.body.appendChild(flashEl) }
  flashEl.textContent = msg
  flashEl.classList.add('show')
  clearTimeout(flash._t)
  flash._t = setTimeout(() => flashEl.classList.remove('show'), 1800)
}

// read-only banner for shared-in (mirrored) notes
let roBanner = null
function setBanner(on, label) {
  if (!roBanner) {
    roBanner = document.createElement('div')
    roBanner.id = 'ro-banner'
    document.body.insertBefore(roBanner, editorEl)
  }
  roBanner.textContent = label || 'Read-only · shared note'
  roBanner.style.display = on ? 'block' : 'none'
}

// --- bridge: C# -> JS ---
window.PaperNote = {
  // payload = { md, html }. md is the source of truth; html is legacy and only used to
  // migrate notes written before the markdown switch (converted once, then saved back).
  load(payload) {
    const md = payload?.md || ''
    const legacyHtml = payload?.html || ''
    const readOnly = !!payload?.readOnly
    let markdown = md
    let migrated = false
    if (!markdown && legacyHtml) { markdown = turndown.turndown(legacyHtml); migrated = true }

    loading = true
    editor.commands.setContent(markdown, false)
    loading = false
    editor.setEditable(!readOnly)
    document.body.classList.toggle('readonly', readOnly)
    setBanner(readOnly, payload?.label || '')
    setUpdatedAt(payload?.updatedAt)
    editorEl.style.visibility = 'visible'
    if (migrated && !readOnly) pushSave()   // never write back a read-only mirror
  },
  clear() {
    loading = true
    editor.commands.clearContent(false)
    loading = false
    editor.setEditable(false)
    document.body.classList.remove('readonly')
    setBanner(false, '')
    setUpdatedAt(null)
    editorEl.style.visibility = 'hidden'
  },
  focus() { editor.commands.focus('end') },
  setFont(kind) { document.body.classList.toggle('serif', kind === 'serif') },
  setTheme(kind) { document.body.classList.toggle('dark', kind === 'dark') },
  setRuled(on) { document.body.classList.toggle('ruled', !!on) },
  toMarkdown() { return editor.storage.markdown.getMarkdown() },
  aiKeyStatus(result) { resolveHost('ai-ensure-key', result) },
  aiChunkDone(result) { resolveHost('ai-chunk', result) },
  aiFinalDone(result) { resolveHost('ai-final', result) },
  aiEnhanceDone(result) { resolveHost('ai-enhance', result) },
  openTemplatePicker() { openTemplatePicker() }
}

// Close the template picker on Escape.
document.addEventListener('keydown', e => {
  if (!templatePicker.hidden && e.key === 'Escape') { closeTemplatePicker(); e.preventDefault() }
}, true)

function setAiStatus(message) {
  let status = document.getElementById('ai-status')
  if (!status) {
    status = document.createElement('div')
    status.id = 'ai-status'
    status.innerHTML = '<span></span>'
    document.body.appendChild(status)
  }
  status.querySelector('span').textContent = message
  status.hidden = !message
}

let metaUnix = null

function setUpdatedAt(unixSeconds) {
  metaUnix = unixSeconds || null
  renderMeta()
}

// Meta line = "<date> · <n> words · <m> min read". Word count updates live as you type;
// reading time assumes ~200 wpm. Count is hidden for an empty note.
function renderMeta() {
  if (!noteMeta) return

  const parts = []
  if (metaUnix) {
    const date = new Date(metaUnix * 1000)
    const day = date.toLocaleDateString(undefined, { day: 'numeric', month: 'long', year: 'numeric' })
    const time = date.toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' })
    parts.push(`${day} at ${time}`)
  }

  const words = wordCount()
  if (words > 0)
    parts.push(`${words.toLocaleString()} ${words === 1 ? 'word' : 'words'} · ${Math.max(1, Math.ceil(words / 200))} min read`)

  noteMeta.textContent = parts.join('  ·  ')
  noteMeta.hidden = parts.length === 0
}

function wordCount() {
  const text = editor.getText().trim()
  return text ? text.split(/\s+/).length : 0
}

// HTML -> Markdown for export (GFM: tables, task lists, strikethrough).
const turndown = new TurndownService({ headingStyle: 'atx', codeBlockStyle: 'fenced', bulletListMarker: '-' })
turndown.use(gfm)
// Carry the code block language into the fence (```json etc.).
turndown.addRule('fencedCodeLang', {
  filter: node => node.nodeName === 'PRE' && node.firstChild?.nodeName === 'CODE',
  replacement: (_content, node) => {
    const code = node.firstChild
    const cls = code.getAttribute('class') || ''
    const lang = (cls.match(/language-(\w+)/) || [, ''])[1]
    return `\n\n\`\`\`${lang}\n${code.textContent}\n\`\`\`\n\n`
  }
})

// --- paste / drop images (downscaled, embedded as base64) ---
const MAX_DIM = 1600

function insertImageFile(file) {
  const reader = new FileReader()
  reader.onload = () => {
    const img = new window.Image()
    img.onload = () => {
      let { width, height } = img
      const scale = Math.min(1, MAX_DIM / Math.max(width, height))
      width = Math.round(width * scale)
      height = Math.round(height * scale)
      const canvas = document.createElement('canvas')
      canvas.width = width
      canvas.height = height
      canvas.getContext('2d').drawImage(img, 0, 0, width, height)
      const dataUrl = canvas.toDataURL('image/png')
      editor.chain().focus().setImage({ src: dataUrl }).run()
    }
    img.src = reader.result
  }
  reader.readAsDataURL(file)
}

editorEl.addEventListener('paste', e => {
  const items = [...(e.clipboardData?.items || [])]
  const image = items.find(i => i.type.startsWith('image/'))
  if (image) {
    e.preventDefault()
    const file = image.getAsFile()
    if (file) insertImageFile(file)
    return
  }

  // Skip when already typing inside a code block (let the raw paste through).
  if (editor.isActive('codeBlock')) return

  // Excel / Sheets put a real <table> in the HTML flavor; the default parser
  // builds a proper editor table from it, so step aside.
  const html = e.clipboardData?.getData('text/html')
  if (html && /<table/i.test(html)) return

  const text = e.clipboardData?.getData('text/plain')
  if (!text) return

  // Tab-separated plain text (terminal, CSV tools, some grids) has no HTML
  // flavor - build the table ourselves. First row becomes the header.
  const rows = detectTsv(text)
  if (rows) {
    e.preventDefault()
    editor.chain().focus().insertContent(buildTable(rows)).run()
    return
  }

  // Smart paste: JSON / XML / YAML -> formatted, language-tagged code block.
  const detected = detectCode(text)
  if (!detected) return

  e.preventDefault()
  editor.chain().focus().insertContent({
    type: 'codeBlock',
    attrs: { language: detected.lang },
    content: [{ type: 'text', text: detected.code }]
  }).run()
}, true)

// Tab-separated text -> rows, or null when it doesn't look tabular.
// Every non-empty line must contain a tab and at least 2 columns overall.
function detectTsv(text) {
  const lines = text.replace(/\r/g, '').split('\n').filter(l => l.trim().length)
  if (!lines.length || !lines.every(l => l.includes('\t'))) return null

  const rows = lines.map(l => l.split('\t').map(c => c.trim()))
  const cols = Math.max(...rows.map(r => r.length))
  if (cols < 2) return null

  return rows.map(r => { while (r.length < cols) r.push(''); return r })
}

function buildTable(rows) {
  return {
    type: 'table',
    content: rows.map((cells, rowIndex) => ({
      type: 'tableRow',
      content: cells.map(cell => ({
        type: rowIndex === 0 ? 'tableHeader' : 'tableCell',
        content: [{
          type: 'paragraph',
          content: cell ? [{ type: 'text', text: cell }] : []
        }]
      }))
    }))
  }
}

// Detect a structured format in pasted text and return {lang, formatted code}, or null.
function detectCode(raw) {
  const t = raw.trim()
  if (t.length < 2) return null

  if (t[0] === '{' || t[0] === '[') {
    try { return { lang: 'json', code: JSON.stringify(JSON.parse(t), null, 2) } } catch {}
  }
  if (t[0] === '<' && t[t.length - 1] === '>') {
    const doc = new DOMParser().parseFromString(t, 'application/xml')
    if (!doc.querySelector('parsererror')) return { lang: 'xml', code: formatXml(t) }
  }
  if (looksLikeYaml(t)) return { lang: 'yaml', code: t }   // YAML is whitespace-sensitive; don't reflow
  return null
}

// Indent XML by nesting depth. Keeps simple <a>text</a> on one line.
function formatXml(xml) {
  const withBreaks = xml.replace(/>\s*</g, '>\n<')
  let depth = 0
  return withBreaks.split('\n').map(line => {
    line = line.trim()
    if (/^<\//.test(line)) depth = Math.max(0, depth - 1)
    const out = '  '.repeat(depth) + line
    const opensBlock = /^<[^!?][^>]*[^/]>$/.test(line) && !/^<.*<\/.*>$/.test(line)
    if (opensBlock) depth++
    return out
  }).join('\n')
}

// Conservative YAML check: '---' start, or mostly 'key:' / '- ' lines.
function looksLikeYaml(t) {
  if (t.startsWith('---')) return true
  const lines = t.split('\n').filter(l => l.trim() && !l.trim().startsWith('#'))
  if (lines.length < 2) return false
  const structured = lines.filter(l => /^\s*[\w.-]+\s*:(\s|$)/.test(l) || /^\s*-\s+/.test(l))
  return structured.length >= 2 && structured.length >= lines.length * 0.7
}

editorEl.addEventListener('drop', e => {
  const files = [...(e.dataTransfer?.files || [])].filter(f => f.type.startsWith('image/'))
  if (files.length) {
    e.preventDefault()
    files.forEach(insertImageFile)
  }
}, true)

// --- drop .md / .txt files anywhere -> import each as a new note (handled by C#) ---
// Image drops (above) stay in the editor; these become separate notes. The browser can't
// expose a filesystem path, so we read the text here and hand the content to C#.
const IMPORT_FILE_EXT = /\.(md|markdown|txt)$/i

document.addEventListener('dragover', e => {
  if ([...(e.dataTransfer?.items || [])].some(i => i.kind === 'file')) e.preventDefault()
}, true)

document.addEventListener('drop', e => {
  const files = [...(e.dataTransfer?.files || [])].filter(f => IMPORT_FILE_EXT.test(f.name))
  if (!files.length) return
  e.preventDefault()
  Promise.all(files.map(readTextFile)).then(items => {
    const good = items.filter(Boolean)
    if (good.length) host?.postMessage({ type: 'import-files', files: good })
  })
}, true)

function readTextFile(file) {
  return new Promise(resolve => {
    const reader = new FileReader()
    reader.onload = () => resolve({ name: file.name, content: String(reader.result || '') })
    reader.onerror = () => resolve(null)
    reader.readAsText(file)
  })
}

// keep the floating table bar pinned while scrolling
window.addEventListener('scroll', updateTableTools, true)

// tell host we're ready
host?.postMessage({ type: 'ready' })
