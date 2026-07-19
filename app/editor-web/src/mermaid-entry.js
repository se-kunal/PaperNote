// Separate bundle: loaded on demand the first time a Mermaid diagram is rendered, so the
// heavy mermaid library stays out of the main editor bundle and off the startup path.
import mermaid from 'mermaid'

window.__mermaid = mermaid
