// Bundle the TipTap editor into a single offline JS file + copy static assets to dist/.
import { build } from 'esbuild'
import { copyFileSync, mkdirSync } from 'fs'
import { join } from 'path'

const dir = import.meta.dirname
const dist = join(dir, 'dist')
mkdirSync(dist, { recursive: true })

await build({
  entryPoints: [join(dir, 'src', 'editor.js')],
  bundle: true,
  format: 'iife',
  minify: true,
  outfile: join(dist, 'editor.js')
})

// Mermaid in its own file — lazy-loaded via <script> only when the first diagram renders.
await build({
  entryPoints: [join(dir, 'src', 'mermaid-entry.js')],
  bundle: true,
  format: 'iife',
  minify: true,
  outfile: join(dist, 'mermaid.js')
})

copyFileSync(join(dir, 'src', 'editor.html'), join(dist, 'editor.html'))
copyFileSync(join(dir, 'src', 'editor.css'), join(dist, 'editor.css'))
console.log('editor bundled -> dist/')
