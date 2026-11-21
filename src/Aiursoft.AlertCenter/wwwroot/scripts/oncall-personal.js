(function () {
    'use strict';
    function updateCountdowns() {
        document.querySelectorAll('[data-countdown]').forEach(function (element) {
            const minutes = Math.max(0, Math.ceil((new Date(element.dataset.countdown).getTime() - Date.now()) / 60000));
            const values = { days: Math.floor(minutes / 1440), hours: Math.floor(minutes % 1440 / 60), minutes: minutes % 60 };
            const duration = typeof Intl.DurationFormat === 'function'
                ? new Intl.DurationFormat(undefined, { style: 'short' }).format(values)
                : Object.entries(values).map(function (entry) {
                    return new Intl.NumberFormat(undefined, { style: 'unit', unit: entry[0].slice(0, -1), unitDisplay: 'short' }).format(entry[1]);
                }).join(' ');
            element.textContent = element.dataset.label + ': ' + duration;
        });
    }
    updateCountdowns();
    if (document.querySelector('[data-countdown]')) window.setInterval(updateCountdowns, 30000);
    const copy = document.querySelector('[data-copy-subscription]');
    if (copy) copy.addEventListener('click', async function () {
        const input = document.getElementById('oncall-subscription');
        const status = document.getElementById('oncall-copy-status');
        try {
            await navigator.clipboard.writeText(input.value);
            status.textContent = copy.dataset.copied;
        } catch {
            input.select();
            status.textContent = copy.dataset.copyFailed;
        }
    });
}());
