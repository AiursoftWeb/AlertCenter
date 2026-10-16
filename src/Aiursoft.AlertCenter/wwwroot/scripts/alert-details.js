(() => {
function setupCopy(buttonId, textSelector, isCodeTag = false) {
            const btn = document.getElementById(buttonId);
            if (!btn) return;
            btn.addEventListener('click', async function () {
                const el = document.querySelector(textSelector);
                if (!el) return;
                const text = isCodeTag ? el.innerText : el.textContent;
                try {
                    await navigator.clipboard.writeText(text);
                    const original = this.innerHTML;
                    this.innerHTML = '<i class="align-middle me-1" data-lucide="check"></i>';
                    this.appendChild(document.createTextNode(getLocalizedText("copied", "Copied")));
                    setTimeout(() => {

                        this.innerHTML = original;
                        if (window.lucide) lucide.createIcons({ nodes: [this] });
                    }, 2000);
                } catch {
                    // ignore
                }
            });
        }

        setupCopy('copy-content-btn', '#message-content-text');
        setupCopy('copy-json-btn', '#context-json-code', true);
        setupCopy('copy-report-id', '#report-id-val');
})();
