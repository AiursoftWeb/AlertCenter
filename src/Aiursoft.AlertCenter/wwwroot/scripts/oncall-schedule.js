(() => {
    const localTime = (utc, zone) => {
        const parts = new Intl.DateTimeFormat('sv-SE', { timeZone: zone, year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', hourCycle: 'h23' }).formatToParts(new Date(utc));
        const get = type => parts.find(part => part.type === type).value;
        return `${get('year')}-${get('month')}-${get('day')}T${get('hour')}:${get('minute')}`;
    };
    const wallTime = value => Date.parse(`${value}:00Z`);
    const wallString = value => new Date(value).toISOString().slice(0, 16);
    function copyRange(slots, sourceStart, sourceEnd, start, end, members) {
        const period = wallTime(sourceEnd) - wallTime(sourceStart);
        const from = wallTime(start), until = wallTime(end);
        if (!(period > 0 && until > from)) throw new Error('range');
        if (slots.some(slot => !(wallTime(slot.endLocal) > wallTime(slot.startLocal)))) throw new Error('range');
        if (slots.some(slot => slot.userIds.some(id => !members.some(member => member.id === id && !member.departed)))) throw new Error('departed');
        const copies = [];
        for (let cycle = from; cycle < until; cycle += period) {
            for (const slot of slots) {
                const left = Math.max(wallTime(slot.startLocal), wallTime(sourceStart));
                const right = Math.min(wallTime(slot.endLocal), wallTime(sourceEnd));
                const copyStart = cycle + left - wallTime(sourceStart);
                const copyEnd = Math.min(until, cycle + right - wallTime(sourceStart));
                if (copyEnd > copyStart) copies.push({ startLocal: wallString(copyStart), endLocal: wallString(copyEnd), userIds: [...slot.userIds] });
            }
        }
        return copies;
    }
    function hoursFor(slots, id, start, end) {
        const intervals = slots.filter(slot => slot.userIds.includes(id)).map(slot => [Math.max(Date.parse(start), Date.parse(slot.startsAtUtc)), Math.min(Date.parse(end), Date.parse(slot.endsAtUtc))])
            .filter(([left, right]) => right > left).sort((a, b) => a[0] - b[0]);
        let total = 0, covered = -Infinity;
        for (const [left, right] of intervals) {
            total += Math.max(0, right - Math.max(left, covered));
            covered = Math.max(covered, right);
        }
        return total / 3600000;
    }
    if (typeof module !== 'undefined') module.exports = { localTime, copyRange, hoursFor };
    if (typeof document === 'undefined') return;
    const root = document.getElementById('oncall-schedule');
    if (!root) return;
    const get = id => document.getElementById(id);
    const text = (key, fallback) => getLocalizedText(key, fallback);
    const members = JSON.parse(root.dataset.members);
    const initial = JSON.parse(root.dataset.slots);
    const zone = root.dataset.zone;
    const start = root.dataset.start, end = root.dataset.end;
    const prepare = slots => slots.map(slot => ({ ...slot, userIds: [...slot.userIds], startLocal: localTime(slot.startsAtUtc, zone), endLocal: localTime(slot.endsAtUtc, zone) }));
    let slots = prepare(initial), dirty = false, busy = false, copied = false, sequence = 0, rangeSequence = 0, timer, pending = null;
    function list(id, values) {
        const items = values.map(value => { const item = document.createElement('li'); item.textContent = value; return item; });
        get(id).replaceChildren(...items);
    }
    function payload() {
        const submitted = structuredClone(slots);
        for (const slot of submitted) {
            if (slot.startsAtUtc && slot.startLocal === localTime(slot.startsAtUtc, zone)) delete slot.startLocal;
            if (slot.endsAtUtc && slot.endLocal === localTime(slot.endsAtUtc, zone)) delete slot.endLocal;
        }
        return { groupId: root.dataset.groupId, version: root.dataset.version, startUtc: start, endUtc: end, slots: submitted };
    }
    async function post(url, body) {
        const response = await fetch(url, { method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json', RequestVerificationToken: root.querySelector('[name="__RequestVerificationToken"]').value }, body: JSON.stringify(body) });
        const data = await response.json();
        if (!response.ok) throw new Error((data.errors || [text('schedule-save-failed', 'Unable to save schedule. Refresh and try again.')]).join('\n'));
        return data;
    }
    function cancelPending() {
        rangeSequence++;
        pending = null;
        get('oncall-replace-confirm').hidden = true;
    }
    function changed() {
        dirty = true;
        sequence++;
        cancelPending();
        get('oncall-draft-notice').hidden = false;
        clearTimeout(timer);
        timer = setTimeout(preview, 250);
    }
    function renderRows() {
        const rows = slots.map((slot, index) => {
            const row = document.createElement('tr');
            for (const [key, label] of [['startLocal', 'Start'], ['endLocal', 'End']]) {
                const cell = document.createElement('td'), input = document.createElement('input');
                input.type = 'datetime-local'; input.className = 'form-control'; input.value = slot[key];
                input.setAttribute('aria-label', text(label.toLowerCase(), label));
                input.addEventListener('input', () => { slot[key] = input.value; changed(); });
                cell.append(input); row.append(cell);
            }
            const people = document.createElement('td');
            for (const member of members) {
                if (member.departed && !slot.userIds.includes(member.id)) continue;
                const label = document.createElement('label'), checkbox = document.createElement('input');
                label.className = 'd-block text-nowrap'; checkbox.type = 'checkbox'; checkbox.className = 'form-check-input me-2';
                checkbox.value = member.id; checkbox.checked = slot.userIds.includes(member.id);
                checkbox.addEventListener('change', () => {
                    slot.userIds = checkbox.checked ? [...new Set([...slot.userIds, member.id])] : slot.userIds.filter(id => id !== member.id);
                    changed();
                });
                label.append(checkbox, document.createTextNode(member.name + (member.departed ? ` (${text('oncall-departed-label', 'Departed')})` : ''))); people.append(label);
            }
            row.append(people);
            const actions = document.createElement('td'), remove = document.createElement('button');
            remove.type = 'button'; remove.className = 'btn btn-sm btn-outline-danger'; remove.textContent = text('delete-shift', 'Delete shift');
            remove.addEventListener('click', () => { slots.splice(index, 1); renderRows(); changed(); });
            actions.append(remove); row.append(actions);
            return row;
        });
        get('oncall-shift-rows').replaceChildren(...rows);
    }
    function draw(data) {
        const chart = get('oncall-gantt');
        chart.querySelectorAll('[data-draft-shift]').forEach(bar => bar.remove());
        const duration = Date.parse(end) - Date.parse(start);
        const assigned = data.slots.filter(slot => slot.userIds.length);
        const stats = {
            'oncall-total-shifts': assigned.length,
            'oncall-total-staff': new Set(assigned.flatMap(slot => slot.userIds)).size,
            'oncall-total-hours': `${assigned.reduce((sum, slot) => sum + Math.max(0, Math.min(Date.parse(end), Date.parse(slot.endsAtUtc)) - Math.max(Date.parse(start), Date.parse(slot.startsAtUtc))) / 3600000, 0).toFixed(2)} ${text('oncall-hours-unit', 'hours')}`
        };
        for (const [id, value] of Object.entries(stats)) if (get(id)) get(id).textContent = value;
        const lane = get('oncall-warning-lane');
        lane.replaceChildren();
        for (const warning of data.timelineWarnings) {
            const marker = document.createElement('button');
            marker.type = 'button'; marker.className = 'ac-gantt-shift bg-warning';
            const left = Math.max(Date.parse(start), Date.parse(warning.startsAtUtc)), right = Math.min(Date.parse(end), Date.parse(warning.endsAtUtc));
            if (right <= left) continue;
            marker.style.left = `${(left - Date.parse(start)) / duration * 100}%`; marker.style.width = `${(right - left) / duration * 100}%`;
            marker.dataset.summary = warning.message; marker.dataset.start = localTime(warning.startsAtUtc, zone); marker.dataset.end = localTime(warning.endsAtUtc, zone);
            marker.title = warning.message; marker.setAttribute('aria-label', warning.message); lane.append(marker);
        }
        for (const row of chart.querySelectorAll('[data-user-id]')) {
            const id = row.dataset.userId;
            row.querySelector('.ac-gantt-hours').textContent = `${hoursFor(data.slots, id, start, end).toFixed(2)} ${text('oncall-hours-unit', 'hours')}`;
            for (const slot of data.slots.filter(item => item.userIds.includes(id))) {
                const bar = document.createElement('button');
                bar.type = 'button'; bar.className = `ac-gantt-shift ac-oncall-color-${row.dataset.color}`;
                bar.dataset.draftShift = 'true'; bar.dataset.readonly = 'true';
                const left = Math.max(Date.parse(start), Date.parse(slot.startsAtUtc)), right = Math.min(Date.parse(end), Date.parse(slot.endsAtUtc));
                if (right <= left) continue;
                bar.style.left = `${(left - Date.parse(start)) / duration * 100}%`;
                bar.style.width = `${(right - left) / duration * 100}%`;
                bar.dataset.start = localTime(slot.startsAtUtc, zone); bar.dataset.end = localTime(slot.endsAtUtc, zone);
                bar.dataset.summary = `${row.dataset.name} · ${zone}`;
                bar.title = `${bar.dataset.summary} · ${bar.dataset.start} → ${bar.dataset.end}`;
                bar.setAttribute('aria-label', bar.title);
                row.querySelector('.ac-gantt-lane').append(bar);
            }
        }
        chart.dispatchEvent(new Event('oncall-layout'));
    }
    async function preview() {
        const revision = ++sequence;
        try {
            const data = await post(root.dataset.previewUrl, payload());
            if (revision !== sequence) return;
            list('oncall-schedule-errors', []); list('oncall-schedule-warnings', data.warnings);
            draw(data);
        } catch (error) {
            if (revision === sequence) list('oncall-schedule-errors', [error.message]);
        }
    }
    async function write(body, generate, rangeWrite = false) {
        const data = await post(generate ? root.dataset.generateUrl : root.dataset.saveUrl, body);
        if (rangeWrite && dirty && !generate) {
            root.dataset.version = data.version;
            copied = true;
            cancelPending();
            await preview();
            return;
        }
        dirty = false;
        window.location.reload();
    }
    async function operation(action) {
        if (busy) return;
        busy = true;
        root.inert = true;
        get('oncall-save-schedule').disabled = true;
        get('oncall-replace-apply').disabled = true;
        try { await action(); }
        catch (error) { list('oncall-schedule-errors', [error.message]); }
        finally { busy = false; root.inert = false; get('oncall-save-schedule').disabled = false; get('oncall-replace-apply').disabled = false; }
    }
    async function range(generate) {
        clearTimeout(timer);
        sequence++;
        cancelPending();
        const revision = rangeSequence;
        if (generate && dirty) throw new Error(text('save-draft-first', 'Save or reset the draft before generating a range.'));
        const body = { groupId: root.dataset.groupId, version: root.dataset.version, startLocal: get('oncall-range-start').value, endLocal: get('oncall-range-end').value, slots: [] };
        if (!(wallTime(body.endLocal) > wallTime(body.startLocal))) throw new Error(text('invalid-range', 'End must be after start.'));
        if (!generate) {
            try { body.slots = copyRange(slots, localTime(start, zone), localTime(end, zone), body.startLocal, body.endLocal, members); }
            catch (error) { throw new Error(error.message === 'departed' ? text('cannot-copy-departed', 'Cannot copy assignments for departed team members.') : text('invalid-range', 'End must be after start.')); }
        }
        const data = await post(`${root.dataset.previewUrl}?generate=${generate}`, body);
        if (revision !== rangeSequence) return;
        list('oncall-schedule-warnings', data.warnings);
        list('oncall-schedule-errors', []);
        if (data.replacedSlots.length) {
            list('oncall-replaced-shifts', data.replacedSlots.map(slot => `${localTime(slot.startsAtUtc, zone)} → ${localTime(slot.endsAtUtc, zone)} · ${slot.userIds.map(id => members.find(member => member.id === id)?.name || id).join(', ') || text('unassigned', 'Unassigned')}`));
            pending = { body, generate };
            get('oncall-replace-confirm').hidden = false;
        } else await write(body, generate, true);
    }
    get('oncall-add-shift').addEventListener('click', () => {
        slots.push({ startLocal: localTime(start, zone), endLocal: localTime(end, zone), userIds: [] }); renderRows(); changed();
    });
    get('oncall-save-schedule').addEventListener('click', () => operation(() => write(payload(), false)));
    get('oncall-reset-schedule').addEventListener('click', () => {
        dirty = false;
        if (copied) { window.location.reload(); return; }
        slots = prepare(initial); sequence++; cancelPending(); renderRows(); get('oncall-draft-notice').hidden = true; preview();
    });
    get('oncall-copy-range').addEventListener('click', () => operation(() => range(false)));
    get('oncall-generate')?.addEventListener('click', () => operation(() => range(true)));
    get('oncall-replace-cancel').addEventListener('click', cancelPending);
    get('oncall-replace-apply').addEventListener('click', () => operation(async () => {
        if (pending) await write({ ...pending.body, confirmReplace: true }, pending.generate, true);
    }));
    for (const id of ['oncall-range-start', 'oncall-range-end']) get(id).addEventListener('input', cancelPending);
    get('oncall-range-start').value = localTime(end, zone);
    get('oncall-range-end').value = wallString(wallTime(localTime(end, zone)) + 7 * 86400000);
    window.addEventListener('beforeunload', event => { if (dirty) { event.preventDefault(); event.returnValue = ''; } });
    renderRows();
    preview();
})();
