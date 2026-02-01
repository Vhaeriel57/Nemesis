/**
 * Nemesis Syntax Highlighter for C# code
 * Provides VS Code-like syntax highlighting without external dependencies
 */

window.NemesisSyntax = {
    // C# keywords
    keywords: [
        'abstract', 'as', 'base', 'bool', 'break', 'byte', 'case', 'catch',
        'char', 'checked', 'class', 'const', 'continue', 'decimal', 'default',
        'delegate', 'do', 'double', 'else', 'enum', 'event', 'explicit', 'extern',
        'false', 'finally', 'fixed', 'float', 'for', 'foreach', 'goto', 'if',
        'implicit', 'in', 'int', 'interface', 'internal', 'is', 'lock', 'long',
        'namespace', 'new', 'null', 'object', 'operator', 'out', 'override',
        'params', 'private', 'protected', 'public', 'readonly', 'ref', 'return',
        'sbyte', 'sealed', 'short', 'sizeof', 'stackalloc', 'static', 'string',
        'struct', 'switch', 'this', 'throw', 'true', 'try', 'typeof', 'uint',
        'ulong', 'unchecked', 'unsafe', 'ushort', 'using', 'virtual', 'void',
        'volatile', 'while', 'async', 'await', 'var', 'dynamic', 'yield',
        'partial', 'get', 'set', 'add', 'remove', 'value', 'where', 'select',
        'from', 'orderby', 'ascending', 'descending', 'group', 'by', 'into',
        'join', 'on', 'equals', 'let', 'nameof', 'when', 'init', 'record',
        'with', 'and', 'or', 'not', 'required'
    ],

    // Unity/C# common types
    types: [
        'GameObject', 'Transform', 'Vector2', 'Vector3', 'Vector4', 'Quaternion',
        'Color', 'Color32', 'Rect', 'RectTransform', 'Canvas', 'Image', 'Text',
        'Button', 'Sprite', 'Material', 'Shader', 'Texture', 'Texture2D',
        'AudioSource', 'AudioClip', 'Rigidbody', 'Rigidbody2D', 'Collider',
        'Collider2D', 'BoxCollider', 'SphereCollider', 'CapsuleCollider',
        'MeshRenderer', 'SkinnedMeshRenderer', 'Animator', 'Animation',
        'Camera', 'Light', 'ParticleSystem', 'TrailRenderer', 'LineRenderer',
        'MonoBehaviour', 'ScriptableObject', 'Component', 'Behaviour',
        'NetworkBehaviour', 'NetworkManager', 'NetworkIdentity', 'SyncVar',
        'Command', 'ClientRpc', 'TargetRpc', 'SyncList', 'SyncDictionary',
        'List', 'Dictionary', 'HashSet', 'Queue', 'Stack', 'Array',
        'Action', 'Func', 'Task', 'IEnumerator', 'IEnumerable', 'Coroutine',
        'WaitForSeconds', 'WaitForEndOfFrame', 'WaitUntil', 'Time', 'Debug',
        'Input', 'Physics', 'Physics2D', 'LayerMask', 'Ray', 'RaycastHit',
        'String', 'Int32', 'Int64', 'Single', 'Double', 'Boolean', 'Byte',
        'StringBuilder', 'Exception', 'EventArgs', 'CancellationToken',
        'SerializeField', 'Header', 'Tooltip', 'Range', 'HideInInspector',
        'RequireComponent', 'DisallowMultipleComponent', 'ExecuteInEditMode'
    ],

    // Control flow keywords (different color)
    controlFlow: [
        'if', 'else', 'switch', 'case', 'default', 'for', 'foreach', 'while',
        'do', 'break', 'continue', 'return', 'throw', 'try', 'catch', 'finally',
        'goto', 'yield'
    ],

    /**
     * Highlight C# code
     */
    highlightCSharp: function(code) {
        if (!code) return code;

        // Escape HTML first
        let escaped = this.escapeHtml(code);

        // Store strings and comments to protect them
        const strings = [];
        const comments = [];

        // Extract and protect string literals
        escaped = escaped.replace(/@"(?:[^"\\]|\\.)*"|"(?:[^"\\]|\\.)*"/g, (match) => {
            strings.push(match);
            return `__STRING_${strings.length - 1}__`;
        });

        // Extract and protect comments
        escaped = escaped.replace(/\/\/[^\n]*/g, (match) => {
            comments.push(match);
            return `__COMMENT_${comments.length - 1}__`;
        });

        escaped = escaped.replace(/\/\*[\s\S]*?\*\//g, (match) => {
            comments.push(match);
            return `__COMMENT_${comments.length - 1}__`;
        });

        // Highlight numbers
        escaped = escaped.replace(/\b(\d+\.?\d*f?)\b/g, '<span class="code-number">$1</span>');

        // Highlight types (before keywords to catch class names)
        this.types.forEach(type => {
            const regex = new RegExp(`\\b(${type})\\b`, 'g');
            escaped = escaped.replace(regex, '<span class="code-type">$1</span>');
        });

        // Highlight control flow keywords
        this.controlFlow.forEach(kw => {
            const regex = new RegExp(`\\b(${kw})\\b`, 'g');
            escaped = escaped.replace(regex, '<span class="code-control">$1</span>');
        });

        // Highlight other keywords
        this.keywords.filter(k => !this.controlFlow.includes(k)).forEach(kw => {
            const regex = new RegExp(`\\b(${kw})\\b`, 'g');
            escaped = escaped.replace(regex, '<span class="code-keyword">$1</span>');
        });

        // Highlight method calls (word followed by parenthesis)
        escaped = escaped.replace(/\b([A-Z][a-zA-Z0-9]*)\s*\(/g, '<span class="code-method">$1</span>(');

        // Highlight attributes [Something]
        escaped = escaped.replace(/\[([^\]]+)\]/g, '[<span class="code-attribute">$1</span>]');

        // Restore comments with highlighting
        comments.forEach((comment, i) => {
            escaped = escaped.replace(`__COMMENT_${i}__`, `<span class="code-comment">${comment}</span>`);
        });

        // Restore strings with highlighting
        strings.forEach((str, i) => {
            escaped = escaped.replace(`__STRING_${i}__`, `<span class="code-string">${str}</span>`);
        });

        return escaped;
    },

    /**
     * Highlight diff content
     */
    highlightDiff: function(code) {
        if (!code) return code;

        const lines = code.split('\n');
        const highlighted = lines.map(line => {
            if (line.startsWith('+++') || line.startsWith('---')) {
                return `<span class="diff-line-header">${this.escapeHtml(line)}</span>`;
            } else if (line.startsWith('@@')) {
                return `<span class="diff-line-header">${this.escapeHtml(line)}</span>`;
            } else if (line.startsWith('+')) {
                return `<span class="diff-line-add">${this.escapeHtml(line)}</span>`;
            } else if (line.startsWith('-')) {
                return `<span class="diff-line-remove">${this.escapeHtml(line)}</span>`;
            }
            return this.escapeHtml(line);
        });

        return highlighted.join('\n');
    },

    /**
     * Escape HTML special characters
     */
    escapeHtml: function(text) {
        const div = document.createElement('div');
        div.textContent = text;
        return div.innerHTML;
    },

    /**
     * Process all code blocks in the page
     */
    highlightAll: function() {
        // Find all code blocks
        document.querySelectorAll('pre code').forEach(block => {
            if (block.dataset.highlighted) return;

            const lang = block.className.match(/language-(\w+)/);
            const code = block.textContent;

            if (lang && lang[1] === 'csharp' || lang && lang[1] === 'cs') {
                block.innerHTML = this.highlightCSharp(code);
            } else if (lang && lang[1] === 'diff') {
                block.innerHTML = this.highlightDiff(code);
            }

            block.dataset.highlighted = 'true';
        });

        // Find diff-content divs
        document.querySelectorAll('.diff-content:not([data-highlighted])').forEach(block => {
            block.innerHTML = this.highlightDiff(block.textContent);
            block.dataset.highlighted = 'true';
        });
    },

    /**
     * Copy code to clipboard
     */
    copyCode: function(button) {
        const codeBlock = button.closest('.code-block');
        if (!codeBlock) return;

        const code = codeBlock.querySelector('code');
        if (!code) return;

        navigator.clipboard.writeText(code.textContent).then(() => {
            const originalText = button.textContent;
            button.textContent = 'Copié!';
            setTimeout(() => {
                button.textContent = originalText;
            }, 2000);
        });
    },

    /**
     * Initialize - call from Blazor
     */
    init: function() {
        // Initial highlight
        this.highlightAll();

        // Watch for new content
        const observer = new MutationObserver(() => {
            this.highlightAll();
        });

        observer.observe(document.body, {
            childList: true,
            subtree: true
        });

        console.log('NemesisSyntax initialized');
    }
};

// Auto-init on DOM ready
if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', () => NemesisSyntax.init());
} else {
    NemesisSyntax.init();
}
