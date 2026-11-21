(() => {
    'use strict';

    const localDateKey = date => `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`;
    const dayStart = date => new Date(date.getFullYear(), date.getMonth(), date.getDate());
    const monthStart = date => new Date(date.getFullYear(), date.getMonth(), 1);

    function splitShiftByLocalDate(shift, from = new Date(shift.startsAtUtc), until = new Date(shift.endsAtUtc)) {
        const start = new Date(shift.startsAtUtc), end = new Date(shift.endsAtUtc);
        const segments = [];
        if (!(end > start && until > from && end > from && start < until)) return segments;
        const first = dayStart(new Date(Math.max(start.getTime(), from.getTime())));
        const limit = Math.min(end.getTime(), until.getTime());
        for (let date = first; date.getTime() < limit;) {
            // Advance by calendar dates, not 24 hours: local days may span a DST transition.
            const next = new Date(date.getFullYear(), date.getMonth(), date.getDate() + 1);
            if (end > start && next > start) {
                segments.push({
                    shift, date: localDateKey(date),
                    startsHere: start >= date && start < next,
                    endsHere: end > date && end <= next,
                    start: new Date(Math.max(start.getTime(), date.getTime())),
                    end: new Date(Math.min(end.getTime(), next.getTime()))
                });
            }
            date = next;
        }
        return segments;
    }

    function monthGrid(month) {
        const first = monthStart(month);
        const mondayOffset = (first.getDay() + 6) % 7;
        const days = new Date(first.getFullYear(), first.getMonth() + 1, 0).getDate();
        return Array.from({ length: Math.ceil((mondayOffset + days) / 7) * 7 }, (_, index) => {
            const date = new Date(first.getFullYear(), first.getMonth(), 1 - mondayOffset + index);
            return { date, key: localDateKey(date), inMonth: date.getMonth() === first.getMonth() };
        });
    }

    function monthBounds(now) {
        const until = new Date(now.getTime() + 56 * 86400000);
        return { first: monthStart(now), last: monthStart(until) };
    }

    function navigateMonth(month, offset, bounds) {
        const target = new Date(month.getFullYear(), month.getMonth() + offset, 1);
        return new Date(Math.max(bounds.first.getTime(), Math.min(bounds.last.getTime(), target.getTime())));
    }

    function collapseDay(segments) {
        return { visible: segments.slice(0, 3), hiddenCount: Math.max(0, segments.length - 3) };
    }

    if (typeof module !== 'undefined') module.exports = { localDateKey, splitShiftByLocalDate, monthGrid, monthBounds, navigateMonth, collapseDay };
    if (typeof document === 'undefined') return;
    const root = document.getElementById('oncall-personal-calendar');
    if (!root) return;
    const fallback = document.getElementById('personal-calendar-fallback');
    const text = (key, value) => value === undefined ? root.dataset[key] : root.dataset[key].replace('{0}', value);
    const shifts = JSON.parse(root.dataset.shifts);
    const originals = new Map(Array.from(fallback.querySelectorAll('[data-personal-shift]'), article => [article.dataset.personalShift, article]));
    const teams = [...new Map(shifts.map(shift => [shift.groupId, shift.groupName])).entries()].sort(([a], [b]) => a.localeCompare(b));
    const hues = new Map(teams.map(([id], index) => [id, (index * 137.508 + 215) % 360]));
    const now = new Date();
    const today = localDateKey(now);
    const bounds = monthBounds(now);
    let month = bounds.first;
    const locale = document.documentElement.lang || undefined;
    const dateFormat = new Intl.DateTimeFormat(locale, { dateStyle: 'full' });
    const timeFormat = new Intl.DateTimeFormat(locale, { hour: '2-digit', minute: '2-digit' });
    const instantFormat = new Intl.DateTimeFormat(locale, { dateStyle: 'medium', timeStyle: 'short' });
    const title = root.querySelector('#personal-calendar-month');
    const previous = root.querySelector('[data-calendar-previous]');
    const next = root.querySelector('[data-calendar-next]');
    const details = root.querySelector('[data-calendar-details]');
    const detailsTitle = root.querySelector('#personal-calendar-details-title');
    const detailItems = root.querySelector('[data-calendar-detail-items]');
    const isCurrent = shift => new Date(shift.startsAtUtc) <= now && now < new Date(shift.endsAtUtc);
    const color = (element, id) => element.style.setProperty('--ac-team-hue', hues.get(id));
    const node = (tag, className, content) => {
        const element = document.createElement(tag);
        element.className = className;
        if (content !== undefined) element.textContent = content;
        return element;
    };
    const button = (className, content, label, activate) => {
        const element = node('button', className, content);
        element.type = 'button';
        element.setAttribute('aria-label', label);
        element.addEventListener('click', activate);
        return element;
    };
    function shiftLabel(shift) {
        return `${shift.groupName} · ${text('starts')}: ${instantFormat.format(new Date(shift.startsAtUtc))} · ${text('ends')}: ${instantFormat.format(new Date(shift.endsAtUtc))}${isCurrent(shift) ? ' · ' + text('onDuty') : ''}`;
    }
    function segmentLabel(segment) {
        const parts = [];
        if (segment.startsHere) parts.push(`${text('starts')} ${timeFormat.format(new Date(segment.shift.startsAtUtc))}`);
        if (segment.endsHere) parts.push(`${text('ends')} ${timeFormat.format(new Date(segment.shift.endsAtUtc))}`);
        return parts.length ? parts.join(' · ') : text('allDay');
    }
    function showDetails(day, selected) {
        detailsTitle.textContent = text('shiftsOn', dateFormat.format(day));
        detailItems.replaceChildren();
        if (!selected.length) detailItems.append(node('p', 'text-muted', text('empty')));
        selected.forEach(segment => {
            const article = originals.get(segment.shift.id).cloneNode(true);
            article.classList.add('ac-personal-detail');
            color(article, segment.shift.groupId);
            if (isCurrent(segment.shift)) article.prepend(node('div', 'fw-bold mb-1', text('onDuty')));
            detailItems.append(article);
        });
        root.querySelectorAll('[data-calendar-date]').forEach(element => element.setAttribute('aria-pressed', String(element.dataset.calendarDate === localDateKey(day))));
        details.hidden = false;
        detailsTitle.focus();
    }
    function renderMonth() {
        title.textContent = new Intl.DateTimeFormat(locale, { year: 'numeric', month: 'long' }).format(month);
        previous.disabled = month.getTime() === bounds.first.getTime();
        next.disabled = month.getTime() === bounds.last.getTime();
        details.hidden = true;
        const cells = monthGrid(month);
        const until = new Date(cells[cells.length - 1].date);
        until.setDate(until.getDate() + 1);
        const byDay = new Map(cells.map(cell => [cell.key, []]));
        shifts.forEach(shift => splitShiftByLocalDate(shift, cells[0].date, until).forEach(segment => byDay.get(segment.date).push(segment)));
        const body = document.createDocumentFragment();
        let row;
        cells.forEach((cell, index) => {
            if (index % 7 === 0) {
                row = document.createElement('tr');
                body.append(row);
            }
            const segments = byDay.get(cell.key);
            const td = node('td', 'ac-personal-day');
            if (!cell.inMonth) td.classList.add('is-outside');
            if (cell.key < today) td.classList.add('is-past');
            if (cell.key === today) td.classList.add('is-today');
            const dateLabel = `${dateFormat.format(cell.date)}${cell.key === today ? ' · ' + text('today') : ''} · ${text('showAll', segments.length)}`;
            const dateButton = button('ac-personal-date', cell.date.getDate(), dateLabel + (segments.length ? ' · ' + segments.map(segment => shiftLabel(segment.shift)).join('; ') : ''), () => showDetails(cell.date, segments));
            dateButton.dataset.calendarDate = cell.key;
            dateButton.setAttribute('aria-pressed', 'false');
            if (cell.key === today) dateButton.setAttribute('aria-current', 'date');
            const dots = node('span', 'ac-personal-dots');
            dots.setAttribute('aria-hidden', 'true');
            const collapsed = collapseDay(segments);
            collapsed.visible.forEach(segment => {
                const dot = node('span', 'ac-personal-dot');
                if (isCurrent(segment.shift)) dot.classList.add('is-current');
                color(dot, segment.shift.groupId);
                dots.append(dot);
            });
            if (collapsed.hiddenCount) dots.append(node('span', 'ac-personal-dot-more', `+${collapsed.hiddenCount}`));
            dateButton.append(dots);
            td.append(dateButton);
            const entries = node('div', 'ac-personal-entries');
            collapsed.visible.forEach(segment => {
                const shift = segment.shift;
                const entry = button('ac-personal-entry', undefined, `${dateFormat.format(cell.date)} · ${shiftLabel(shift)} · ${segmentLabel(segment)}`, () => showDetails(cell.date, [segment]));
                color(entry, shift.groupId);
                entry.title = entry.getAttribute('aria-label');
                entry.append(node('span', 'fw-bold', shift.groupName), node('span', 'ac-personal-entry-time', segmentLabel(segment)));
                if (isCurrent(shift)) entry.append(node('span', 'fw-bold', '● ' + text('onDuty')));
                entries.append(entry);
            });
            if (collapsed.hiddenCount) entries.append(button('ac-personal-more', text('more', collapsed.hiddenCount), dateLabel, () => showDetails(cell.date, segments)));
            td.append(entries);
            row.append(td);
        });
        root.querySelector('tbody').replaceChildren(body);
    }

    const header = document.createElement('tr');
    const weekdayFormat = new Intl.DateTimeFormat(locale, { weekday: 'short' });
    const weekdayFull = new Intl.DateTimeFormat(locale, { weekday: 'long' });
    for (let day = 0; day < 7; day++) {
        const date = new Date(2026, 0, 5 + day);
        const th = node('th', '', weekdayFormat.format(date));
        th.scope = 'col';
        th.setAttribute('aria-label', weekdayFull.format(date));
        header.append(th);
    }
    root.querySelector('thead').append(header);
    const legend = root.querySelector('[data-calendar-legend]');
    teams.forEach(([id, name]) => {
        const item = node('span', 'ac-personal-team', name);
        item.setAttribute('role', 'listitem');
        color(item, id);
        legend.append(item);
    });
    root.querySelector('[data-calendar-zone]').textContent = Intl.DateTimeFormat().resolvedOptions().timeZone;
    root.querySelector('[data-calendar-empty]').hidden = shifts.length !== 0;
    previous.addEventListener('click', () => { month = navigateMonth(month, -1, bounds); renderMonth(); });
    next.addEventListener('click', () => { month = navigateMonth(month, 1, bounds); renderMonth(); });
    root.querySelector('[data-calendar-today]').addEventListener('click', () => {
        month = bounds.first;
        renderMonth();
        root.querySelector(`[data-calendar-date="${today}"]`).focus();
    });
    renderMonth();
    root.hidden = false;
    fallback.hidden = true;
})();
