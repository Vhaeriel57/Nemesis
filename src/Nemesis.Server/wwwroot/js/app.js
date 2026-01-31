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

// Copy code from code blocks
window.copyCode = function(button) {
    var codeBlock = button.closest('.code-block');
    if (codeBlock) {
        var code = codeBlock.querySelector('code');
        if (code) {
            var text = code.textContent;
            navigator.clipboard.writeText(text).then(function() {
                var originalText = button.textContent;
                button.textContent = 'Copié!';
                button.style.background = '#00d26a';
                setTimeout(function() {
                    button.textContent = originalText;
                    button.style.background = '';
                }, 1500);
            }).catch(function(err) {
                console.error('Failed to copy code:', err);
                button.textContent = 'Erreur';
                setTimeout(function() {
                    button.textContent = 'Copier';
                }, 1500);
            });
        }
    }
};

// Folder picker - simple prompt dialog
window.pickFolder = function(currentPath) {
    var defaultPath = currentPath || 'C:\\Projects\\MyUnityProject';
    var path = prompt('Enter the Unity project folder path:', defaultPath);
    return path || '';
};
