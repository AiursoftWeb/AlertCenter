(() => {
document.addEventListener("DOMContentLoaded", function () {
            const restartButton = document.querySelector('#restartConfirmModal .btn-restart');
            if (!restartButton) return;

            function startTimer(duration, displayElement) {
                let timer = duration;
                displayElement.textContent = getLocalizedText("restarting-countdown", "Restarting... ({0}s)").replace("{0}", () => String(timer));
                const interval = setInterval(function () {
                    timer--;
                    displayElement.textContent = getLocalizedText("restarting-countdown", "Restarting... ({0}s)").replace("{0}", () => String(timer));
                    if (timer <= 0) {
                        clearInterval(interval);
                        displayElement.textContent = getLocalizedText("reloading-page", "Reloading page...");
                    }
                }, 1000);
            }

            restartButton.addEventListener('click', function () {
                fetch('/System/Shutdown', {
                    method: 'POST'
                }).catch(error => console.error('Error sending shutdown request:', error));

                restartButton.disabled = true;
                const cancelButton = document.querySelector('#restartConfirmModal .btn-secondary');
                if (cancelButton) {
                    cancelButton.disabled = true;
                }
                startTimer(10, restartButton);
                setTimeout(function () {
                    window.location.reload();
                }, 10000);
            });
        });
})();
