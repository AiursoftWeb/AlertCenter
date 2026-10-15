(() => {
    'use strict';

    function showFeedback(feedback, message, variant) {
        const alert = document.createElement('div');
        alert.className = `alert alert-${variant} mb-3`;
        const content = document.createElement('div');
        content.className = 'alert-message';
        content.textContent = message;
        alert.appendChild(content);
        feedback.replaceChildren(alert);
    }

    document.querySelectorAll('button[data-copy-target], button[data-copy-text]').forEach(button => {
        const feedback = document.getElementById(button.dataset.copyFeedback);
        if (!feedback) return;

        button.addEventListener('click', async () => {
            if (button.disabled) return;
            const previousBusy = button.getAttribute('aria-busy');
            button.disabled = true;
            button.setAttribute('aria-busy', 'true');
            try {
                showFeedback(feedback, '正在复制…', 'info');
                const text = button.dataset.copyText ?? document.getElementById(button.dataset.copyTarget)?.textContent;
                if (!text || typeof navigator.clipboard?.writeText !== 'function') {
                    throw new Error('Clipboard or copy source unavailable');
                }
                await navigator.clipboard.writeText(text);
                showFeedback(feedback, '已复制到剪贴板。', 'success');
            } catch {
                showFeedback(feedback, '复制失败，请手动选择并复制内容；如需重试，请检查浏览器剪贴板权限。', 'danger');
            } finally {
                button.disabled = false;
                if (previousBusy === null) {
                    button.removeAttribute('aria-busy');
                } else {
                    button.setAttribute('aria-busy', previousBusy);
                }
            }
        });
    });
})();
