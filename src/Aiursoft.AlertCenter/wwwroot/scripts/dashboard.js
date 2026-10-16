(() => {
document.getElementById('copy-dashboard-curl')?.addEventListener('click', async function () {
            const code = document.getElementById('dashboard-curl-code').innerText;
            try {
                await navigator.clipboard.writeText(code);
                this.innerHTML = '<i class="align-middle me-1" data-lucide="check"></i>';
                this.appendChild(document.createTextNode(getLocalizedText("copied", "Copied")));
                setTimeout(() => {

                    this.innerHTML = '<i class="align-middle me-1" data-lucide="copy"></i>';
                    this.appendChild(document.createTextNode(getLocalizedText("copy", "Copy")));
                    if (window.lucide) lucide.createIcons({ nodes: [this] });
                }, 2000);
            } catch {
                // fallback
            }
        });

        // Update UTC clock every second
        setInterval(() => {
            const clock = document.getElementById('utc-clock');
            if (clock) {
                const now = new Date();
                const pad = n => String(n).padStart(2, '0');
                clock.textContent = `${pad(now.getUTCHours())}:${pad(now.getUTCMinutes())}:${pad(now.getUTCSeconds())} UTC`;
            }
        }, 1000);
})();
