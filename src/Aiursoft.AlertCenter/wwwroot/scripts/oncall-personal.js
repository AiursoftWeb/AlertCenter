(function () {
    'use strict';
    const browserZone = Intl.DateTimeFormat().resolvedOptions().timeZone;
    document.querySelectorAll('[data-oncall-time]').forEach(function (element) {
        const date = new Date(element.dateTime);
        const options = { year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', timeZoneName: 'shortOffset' };
        const team = new Intl.DateTimeFormat(undefined, Object.assign({}, options, { timeZone: element.dataset.zone })).format(date);
        const local = new Intl.DateTimeFormat(undefined, options).format(date);
        if (team !== local) element.append(' · ' + local + ' (' + browserZone + ')');
    });
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
