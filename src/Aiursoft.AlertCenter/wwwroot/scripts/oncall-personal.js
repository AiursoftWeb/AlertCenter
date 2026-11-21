(function () {
    'use strict';
    function calendarDay(date) {
        // Treat local date components as UTC to count calendar days independently of DST.
        return Date.UTC(date.getFullYear(), date.getMonth(), date.getDate()) / 86400000;
    }

    function formatRelativeShiftTime(date, now) {
        const days = calendarDay(date) - calendarDay(now);
        const clock = new Intl.DateTimeFormat(undefined, { hour: '2-digit', minute: '2-digit' }).format(date);
        if (Math.abs(days) <= 1) {
            const relative = new Intl.RelativeTimeFormat(undefined, { numeric: 'auto' }).format(days, 'day');
            return relative + ' ' + clock;
        }
        const dateOptions = Math.abs(days) <= 6 ? { weekday: 'short' } : {
            year: date.getFullYear() === now.getFullYear() ? undefined : 'numeric',
            month: 'short', day: 'numeric'
        };
        return new Intl.DateTimeFormat(undefined, dateOptions).format(date) + ' ' + clock;
    }

    const browserZone = Intl.DateTimeFormat().resolvedOptions().timeZone;
    const now = new Date(Date.now());
    const fullTime = timeZone => new Intl.DateTimeFormat('sv-SE', {
        timeZone, year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', hourCycle: 'h23'
    });
    const browserTime = fullTime(browserZone);
    document.querySelectorAll('[data-oncall-time]').forEach(function (element) {
        const date = new Date(element.dateTime);
        const teamZone = element.dataset.zone;
        const teamTime = fullTime(teamZone);
        const differentZone = teamTime.resolvedOptions().timeZone !== browserZone;
        element.firstElementChild.textContent = formatRelativeShiftTime(date, now);
        element.lastElementChild.classList.toggle('d-none', !differentZone);
        element.title = teamTime.format(date) + ' (' + teamZone + ')';
        if (differentZone) element.title += '\n' + browserTime.format(date) + ' (' + browserZone + ')';
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
