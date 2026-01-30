// Nemesis - Unity AI Assistant - Client Scripts

window.scrollToBottom = function(element) {
    if (element) {
        element.scrollTop = element.scrollHeight;
    }
};

window.copyToClipboard = function(text) {
    navigator.clipboard.writeText(text).then(function() {
        console.log('Copied to clipboard');
    }).catch(function(err) {
        console.error('Failed to copy: ', err);
    });
};

window.downloadFile = function(filename, content, contentType) {
    const blob = new Blob([content], { type: contentType || 'text/plain' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = filename;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    URL.revokeObjectURL(url);
};

// Syntax highlighting for code blocks (simple version)
window.highlightCode = function() {
    document.querySelectorAll('pre code').forEach(function(block) {
        // Add line numbers
        const lines = block.innerHTML.split('\n');
        block.innerHTML = lines.map(function(line, i) {
            return '<span class="line-number">' + (i + 1) + '</span>' + line;
        }).join('\n');
    });
};

// Auto-resize textarea
window.autoResizeTextarea = function(element) {
    element.style.height = 'auto';
    element.style.height = Math.min(element.scrollHeight, 200) + 'px';
};

// Focus trap for modals
window.trapFocus = function(element) {
    const focusableElements = element.querySelectorAll(
        'button, [href], input, select, textarea, [tabindex]:not([tabindex="-1"])'
    );
    const firstFocusable = focusableElements[0];
    const lastFocusable = focusableElements[focusableElements.length - 1];

    element.addEventListener('keydown', function(e) {
        if (e.key === 'Tab') {
            if (e.shiftKey) {
                if (document.activeElement === firstFocusable) {
                    lastFocusable.focus();
                    e.preventDefault();
                }
            } else {
                if (document.activeElement === lastFocusable) {
                    firstFocusable.focus();
                    e.preventDefault();
                }
            }
        }
    });

    firstFocusable.focus();
};

// Local storage helpers
window.storage = {
    get: function(key) {
        try {
            const item = localStorage.getItem('nemesis_' + key);
            return item ? JSON.parse(item) : null;
        } catch {
            return null;
        }
    },
    set: function(key, value) {
        try {
            localStorage.setItem('nemesis_' + key, JSON.stringify(value));
        } catch {
            console.error('Failed to save to local storage');
        }
    },
    remove: function(key) {
        localStorage.removeItem('nemesis_' + key);
    }
};

// Notification helper
window.notify = function(message, type) {
    type = type || 'info';
    const notification = document.createElement('div');
    notification.className = 'notification notification-' + type;
    notification.textContent = message;
    document.body.appendChild(notification);

    setTimeout(function() {
        notification.classList.add('show');
    }, 10);

    setTimeout(function() {
        notification.classList.remove('show');
        setTimeout(function() {
            document.body.removeChild(notification);
        }, 300);
    }, 3000);
};

console.log('Nemesis client scripts loaded');

// Folder picker using File System Access API (modern browsers)
window.pickFolder = async function() {
    try {
        // Check if the File System Access API is available
        if ('showDirectoryPicker' in window) {
            const directoryHandle = await window.showDirectoryPicker({
                mode: 'read'
            });
            return directoryHandle.name;
        } else {
            // Fallback: prompt user to enter path manually
            const path = prompt('Enter the Unity project folder path:', 'C:\\Projects\\MyUnityProject');
            return path || '';
        }
    } catch (err) {
        if (err.name === 'AbortError') {
            // User cancelled the picker
            return '';
        }
        console.error('Folder picker error:', err);
        // Fallback to prompt
        const path = prompt('Enter the Unity project folder path:', 'C:\\Projects\\MyUnityProject');
        return path || '';
    }
};
