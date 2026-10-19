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

        // Display the live clock in the browser's local time zone.
        const clock = document.getElementById('local-clock');
        if (clock) {
            const updateClock = () => { clock.textContent = new Date().toLocaleString(); };
            updateClock();
            setInterval(updateClock, 1000);
        }
})();
