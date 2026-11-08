(() => {
    const formatters = new Map();
    const localTime = (utc, zone) => {
        if (!formatters.has(zone)) formatters.set(zone, new Intl.DateTimeFormat('sv-SE', { timeZone: zone, year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', hourCycle: 'h23' }));
        const parts = formatters.get(zone).formatToParts(new Date(utc));
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
    const minute = 60000, hour = 60 * minute;
    function utcForLocal(value, zone) {
        const wall = wallTime(value);
        if (!Number.isFinite(wall)) return NaN;
        const offsets = new Set([-36, 0, 36].map(offset => {
            const utc = wall + offset * hour;
            return wallTime(localTime(utc, zone)) - utc;
        }));
        const matches = [...offsets].map(offset => wall - offset).filter(utc => localTime(utc, zone) === value);
        return matches.length ? Math.max(...matches) : NaN;
    }
    function timeOf(slot, edge, zone) {
        const utc = slot[edge === 'start' ? 'startsAtUtc' : 'endsAtUtc'];
        const local = slot[`${edge}Local`];
        return utc && (!local || localTime(utc, zone) === local) ? Date.parse(utc) : utcForLocal(local, zone);
    }
    function setTime(slot, edge, utc, zone) {
        slot[edge === 'start' ? 'startsAtUtc' : 'endsAtUtc'] = new Date(utc).toISOString();
        slot[`${edge}Local`] = localTime(utc, zone);
    }
    function snapTime(utc, zone, free = false) {
        const step = (free ? 1 : 30) * minute;
        return utc + Math.round(wallTime(localTime(utc, zone)) / step) * step - wallTime(localTime(utc, zone)) - (utc % minute);
    }
    function shiftTimes(slot, delta, zone) {
        const left = timeOf(slot, 'start', zone), right = timeOf(slot, 'end', zone);
        setTime(slot, 'start', left + delta, zone); setTime(slot, 'end', right + delta, zone);
    }
    function movePerson(slot, from, to) {
        if (from === to) return;
        slot.userIds = to === '' ? [] : [...new Set(slot.userIds.filter(id => id !== from).concat(to))];
    }
    function swapPeople(slots, first, second) {
        for (const slot of slots) slot.userIds = slot.userIds.map(id => id === first ? second : id === second ? first : id);
    }
    function reminders(slots, context, start, end, members, thresholds, zone) {
        const own = slots.map(slot => ({ ...slot, left: timeOf(slot, 'start', zone), right: timeOf(slot, 'end', zone) }))
            .filter(slot => slot.right > slot.left).sort((a, b) => a.left - b.left);
        const result = [];
        const add = (key, left, right, person = '') => {
            left = Math.max(start, left); right = Math.min(end, right);
            if (right > left) result.push({ key, left, right, person });
        };
        let covered = start;
        for (const slot of own) {
            if (slot.userIds.length) {
                if (slot.left > covered) add('gap', covered, slot.left);
                covered = Math.max(covered, slot.right);
            } else add('unassigned-warning', slot.left, slot.right);
            if (thresholds.staff != null && slot.userIds.length < thresholds.staff) add('staff-warning', slot.left, slot.right);
            if (slot.userIds.some(id => !members.some(member => member.id === id && !member.departed))) add('departed-warning', slot.left, slot.right);
            if (own.some(other => other !== slot && other.left < slot.right && other.right > slot.left)) add('overlap', slot.left, slot.right);
        }
        if (covered < end) add('gap', covered, end);
        const all = own.concat(context.map(slot => ({ ...slot, left: Date.parse(slot.startsAtUtc), right: Date.parse(slot.endsAtUtc) })));
        for (const id of new Set(own.flatMap(slot => slot.userIds))) {
            let previous = null, continuous = null, previousOwn = null, continuousOwn = [];
            for (const slot of all.filter(item => item.userIds.includes(id)).sort((a, b) => a.left - b.left)) {
                const isOwn = own.includes(slot);
                if (previous != null && slot.left < previous && (isOwn || previousOwn)) add('overlap', slot.left, Math.min(slot.right, previous), id);
                if (previous != null && thresholds.rest != null && slot.left - previous < thresholds.rest * hour && (isOwn || previousOwn)) {
                    const target = isOwn ? slot : previousOwn;
                    add('rest-warning', target.left, target.right, id);
                }
                if (previous == null || slot.left > previous) { continuous = slot.left; continuousOwn = []; }
                if (isOwn) continuousOwn.push(slot);
                previous = Math.max(previous ?? -Infinity, slot.right);
                if (thresholds.continuous != null && previous - continuous > thresholds.continuous * hour) {
                    for (const ownSlot of continuousOwn) add('continuous-warning', ownSlot.left, ownSlot.right, id);
                }
                previousOwn = isOwn ? slot : null;
            }
        }
        return result;
    }
    if (typeof module !== 'undefined') module.exports = { localTime, copyRange, hoursFor, utcForLocal, timeOf, snapTime, movePerson, swapPeople, reminders };
    if (typeof document === 'undefined') return;
    const root = document.getElementById('oncall-schedule');
    if (!root) return;
    const get = id => document.getElementById(id);
    const text = (key, fallback) => root.querySelector(`[data-schedule-text="${key}"]`)?.textContent || getLocalizedText(key, fallback);
    const members = JSON.parse(root.dataset.members), context = JSON.parse(root.dataset.context || '[]');
    const zone = root.dataset.zone, start = root.dataset.start, end = root.dataset.end;
    const from = Date.parse(start), until = Date.parse(end), duration = until - from;
    const chart = get('oncall-gantt'), dialog = get('oncall-shift-dialog');
    const prepare = items => items.map(slot => ({ ...slot, userIds: [...slot.userIds], startLocal: localTime(slot.startsAtUtc, zone), endLocal: localTime(slot.endsAtUtc, zone) }));
    const original = { slots: prepare(JSON.parse(root.dataset.slots)), ranges: [] };
    let state = structuredClone(original), dirty = false, busy = false, sequence = 0, timer, pending = null, selected = null, weekSelection = null, editing = null, gesture = null, suppressClick = false;
    const undo = [], redo = [];
    const threshold = key => root.dataset[key] == null || root.dataset[key] === '' ? null : Number(root.dataset[key]);
    const thresholds = { rest: threshold('minimumRest'), continuous: threshold('maximumContinuous'), staff: threshold('minimumStaff') };
    const activeMember = id => id === '' || members.some(member => member.id === id && !member.departed);
    const hasDeparted = slot => slot.userIds.some(id => !activeMember(id));
    const valid = slot => timeOf(slot, 'end', zone) > timeOf(slot, 'start', zone);
    const normalized = items => items.filter(valid).map(slot => ({ ...slot, startsAtUtc: new Date(timeOf(slot, 'start', zone)).toISOString(), endsAtUtc: new Date(timeOf(slot, 'end', zone)).toISOString() }));
    function list(id, values) {
        get(id).replaceChildren(...values.map(value => { const item = document.createElement('li'); item.textContent = value; return item; }));
    }
    function error(message) { list('oncall-schedule-errors', [message]); }
    function submitted(items) {
        return items.map(slot => {
            const copy = structuredClone(slot);
            if (copy.startsAtUtc && copy.startLocal === localTime(copy.startsAtUtc, zone)) delete copy.startLocal;
            if (copy.endsAtUtc && copy.endLocal === localTime(copy.endsAtUtc, zone)) delete copy.endLocal;
            return copy;
        });
    }
    function payload() {
        return { groupId: root.dataset.groupId, version: root.dataset.version, startUtc: start, endUtc: end, slots: submitted(state.slots) };
    }
    async function request(url, body) {
        const response = await fetch(url, body ? { method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json', RequestVerificationToken: root.querySelector('[name="__RequestVerificationToken"]').value }, body: JSON.stringify(body) } : { credentials: 'same-origin' });
        const data = await response.json();
        if (!response.ok) throw new Error((data.errors || [text('schedule-save-failed', 'Unable to save schedule. Refresh and try again.')]).join('\n'));
        return data;
    }
    function cancelPending() { pending = null; get('oncall-replace-confirm').hidden = true; }
    function changed(rows = true) {
        sequence++;
        cancelPending();
        dirty = JSON.stringify(state) !== JSON.stringify(original);
        get('oncall-draft-notice').hidden = !dirty;
        get('oncall-undo-schedule').disabled = !undo.length;
        get('oncall-redo-schedule').disabled = !redo.length;
        document.querySelectorAll('[data-oncall-saved-warning]').forEach(item => item.hidden = dirty);
        if (get('oncall-published-status')) get('oncall-published-status').hidden = dirty;
        if (get('oncall-save-state')) {
            get('oncall-save-state').dataset.originalText ??= get('oncall-save-state').textContent;
            get('oncall-save-state').textContent = dirty ? text('draft-range', 'Draft range') : get('oncall-save-state').dataset.originalText;
        }
        list('oncall-schedule-errors', []);
        list('oncall-copy-drafts', state.ranges.map(range => `${text('draft-range', 'Draft range')}: ${localTime(range.start, zone)} → ${localTime(range.end, zone)} · ${zone}`));
        if (rows) renderRows();
        draw();
        clearTimeout(timer);
        timer = setTimeout(preview, 250);
    }
    function commit(action, rows = true) {
        if (busy) return;
        const before = structuredClone(state);
        action();
        if (JSON.stringify(before) === JSON.stringify(state)) return;
        undo.push(before); redo.length = 0;
        changed(rows);
    }
    function memberInputs(container, slot, update) {
        container.replaceChildren();
        for (const member of members) {
            if (member.departed && !slot.userIds.includes(member.id)) continue;
            const label = document.createElement('label'), checkbox = document.createElement('input');
            label.className = 'd-block text-nowrap'; checkbox.type = 'checkbox'; checkbox.className = 'form-check-input me-2';
            checkbox.value = member.id; checkbox.checked = slot.userIds.includes(member.id);
            checkbox.addEventListener('change', () => {
                if (member.departed && checkbox.checked) { checkbox.checked = false; return; }
                update(member.id, checkbox.checked);
            });
            label.append(checkbox, document.createTextNode(member.name + (member.departed ? ` (${text('oncall-departed-label', 'Departed')})` : ''))); container.append(label);
        }
    }
    function renderRows() {
        get('oncall-shift-rows').replaceChildren(...state.slots.map((slot, index) => {
            const row = document.createElement('tr');
            for (const [key, label] of [['startLocal', 'Start'], ['endLocal', 'End']]) {
                const cell = document.createElement('td'), input = document.createElement('input');
                input.type = 'datetime-local'; input.className = 'form-control'; input.value = slot[key];
                input.setAttribute('aria-label', text(label.toLowerCase(), label));
                input.disabled = hasDeparted(slot);
                input.addEventListener('input', () => commit(() => { state.slots[index][key] = input.value; }, false));
                cell.append(input); row.append(cell);
            }
            const people = document.createElement('td');
            memberInputs(people, slot, (id, checked) => commit(() => {
                state.slots[index].userIds = checked ? [...new Set([...state.slots[index].userIds, id])] : state.slots[index].userIds.filter(value => value !== id);
            }));
            row.append(people);
            const actions = document.createElement('td'), remove = document.createElement('button');
            remove.type = 'button'; remove.className = 'btn btn-sm btn-outline-danger'; remove.textContent = text('delete-shift', 'Delete shift');
            remove.addEventListener('click', () => commit(() => { state.slots.splice(index, 1); selected = null; }));
            actions.append(remove); row.append(actions); return row;
        }));
    }
    function position(element, left, right) {
        element.style.left = `${(left - from) / duration * 100}%`;
        element.style.width = `${(right - left) / duration * 100}%`;
    }
    function describe(element, summary, left, right) {
        element.dataset.summary = summary; element.dataset.start = localTime(left, zone); element.dataset.end = localTime(right, zone);
        element.title = `${summary} · ${element.dataset.start} → ${element.dataset.end} · ${zone}`;
        element.setAttribute('aria-label', element.title);
        element.setAttribute('aria-describedby', 'oncall-shift-detail');
    }
    function pack(lane) {
        const tracks = [];
        const bars = [...lane.querySelectorAll('.ac-gantt-shift')].filter(bar => !bar.hidden);
        bars.sort((a, b) => parseFloat(a.dataset.layoutLeft ?? a.style.left) - parseFloat(b.dataset.layoutLeft ?? b.style.left));
        for (const bar of bars) {
            const left = parseFloat(bar.dataset.layoutLeft ?? bar.style.left), right = left + parseFloat(bar.dataset.layoutWidth ?? bar.style.width);
            let track = tracks.findIndex(value => value <= left);
            if (track < 0) track = tracks.length;
            tracks[track] = right;
            bar.style.top = `${track * 72 + 8}px`; bar.style.transform = 'none';
        }
        lane.style.minHeight = `${Math.max(1, tracks.length) * 72}px`;
    }
    function draw(serverWarnings) {
        const focus = document.activeElement;
        const focusIndex = focus?.dataset.slotIndex, focusPerson = focus?.closest('[data-user-id]')?.dataset.userId;
        chart.querySelectorAll('[data-draft-shift]').forEach(bar => bar.remove());
        const resolved = normalized(state.slots), assigned = resolved.filter(slot => slot.userIds.length && Date.parse(slot.endsAtUtc) > from && Date.parse(slot.startsAtUtc) < until);
        const stats = { 'oncall-total-shifts': assigned.length, 'oncall-total-staff': new Set(assigned.flatMap(slot => slot.userIds)).size,
            'oncall-total-hours': `${assigned.reduce((sum, slot) => sum + Math.max(0, Math.min(until, Date.parse(slot.endsAtUtc)) - Math.max(from, Date.parse(slot.startsAtUtc))) / hour, 0).toFixed(2)} ${text('oncall-hours-unit', 'hours')}` };
        for (const [id, value] of Object.entries(stats)) if (get(id)) get(id).textContent = value;
        const staged = state.ranges.flatMap(range => range.slots);
        const history = context.filter(slot => !state.ranges.some(range => Date.parse(slot.startsAtUtc) < range.end && Date.parse(slot.endsAtUtc) > range.start && !slot.external)).concat(staged);
        const warnings = reminders(state.slots, history, from, until, members, thresholds, zone).map(warning => ({
            startsAtUtc: warning.left, endsAtUtc: warning.right, message: `${warning.person ? `${members.find(member => member.id === warning.person)?.name || warning.person}: ` : ''}${text(warning.key, warning.key)}`
        })).concat(serverWarnings || []);
        list('oncall-schedule-warnings', [...new Set(warnings.map(warning => warning.message))]);
        const lane = get('oncall-warning-lane'); lane.replaceChildren();
        const groupedWarnings = new Map();
        for (const warning of warnings) {
            const key = `${new Date(warning.startsAtUtc).getTime()}:${new Date(warning.endsAtUtc).getTime()}`;
            if (!groupedWarnings.has(key)) groupedWarnings.set(key, { ...warning, messages: new Set() });
            groupedWarnings.get(key).messages.add(warning.message);
        }
        for (const warning of groupedWarnings.values()) {
            warning.message = [...warning.messages].join('\n');
            const left = Math.max(from, new Date(warning.startsAtUtc).getTime()), right = Math.min(until, new Date(warning.endsAtUtc).getTime());
            if (right <= left) continue;
            const marker = document.createElement('button'); marker.type = 'button'; marker.className = 'ac-gantt-shift ac-gantt-warning bg-warning text-dark';
            marker.dataset.readonly = 'true'; marker.textContent = warning.message;
            position(marker, left, right); describe(marker, warning.message, left, right); lane.append(marker);
        }
        pack(lane);
        for (const row of chart.querySelectorAll('[data-user-id]')) {
            const id = row.dataset.userId, rowLane = row.querySelector('.ac-gantt-lane');
            const count = resolved.filter(slot => slot.userIds.includes(id) && Date.parse(slot.startsAtUtc) < until && Date.parse(slot.endsAtUtc) > from).length;
            row.querySelector('.ac-gantt-hours').textContent = `${count} · ${hoursFor(resolved, id, start, end).toFixed(2)} ${text('oncall-hours-unit', 'hours')}`;
            rowLane.querySelectorAll('.ac-gantt-adjacent:not([data-draft-shift])').forEach(bar => {
                bar.dataset.replaced = String(state.ranges.some(range => utcForLocal(bar.dataset.start?.slice(0, 16).replace(' ', 'T'), zone) < range.end && utcForLocal(bar.dataset.end?.slice(0, 16).replace(' ', 'T'), zone) > range.start));
            });
            const entries = state.slots.map((slot, index) => ({ slot, index })).concat(staged.map(slot => ({ slot, index: -1 })));
            for (const { slot, index } of entries) {
                if (!valid(slot) || !(slot.userIds.includes(id) || (id === '' && !slot.userIds.length))) continue;
                const left = timeOf(slot, 'start', zone), right = timeOf(slot, 'end', zone);
                for (const region of ['previous', 'current', 'next']) {
                    const low = region === 'previous' ? from - 36 * hour : region === 'next' ? until : from;
                    const high = region === 'previous' ? from : region === 'next' ? until + 36 * hour : until;
                    if (right <= low || left >= high) continue;
                    const editable = index >= 0 && region === 'current';
                    const bar = document.createElement('button'); bar.type = 'button';
                    bar.className = `ac-gantt-shift ${editable ? `ac-oncall-color-${row.dataset.color}` : 'ac-gantt-adjacent'}`;
                    bar.dataset.draftShift = 'true';
                    if (!editable) bar.dataset.readonly = 'true';
                    else { bar.dataset.slotIndex = String(index); bar.setAttribute('aria-pressed', String(selected?.index === index && selected?.person === id)); }
                    if (region !== 'current') { bar.dataset.edge = region; bar.hidden = true; }
                    if (editable && selected?.index === index && selected?.person === id) bar.classList.add('ac-gantt-selected');
                    if (!dirty && left <= Date.now() && Date.now() < right) bar.classList.add('ac-gantt-current');
                    position(bar, Math.max(left, low), Math.min(right, high));
                    describe(bar, `${row.dataset.name}${hasDeparted(slot) ? ` · ${text('oncall-departed-label', 'Departed')}` : ''}${editable ? '' : ` · ${text('read-only', 'Read-only')}`} · ${((right - left) / hour).toFixed(2)} ${text('oncall-hours-unit', 'hours')}`, left, right);
                    bar.textContent = `${localTime(left, zone).replace('T', ' ')} → ${localTime(right, zone).replace('T', ' ')}`;
                    if (editable) {
                        bar.setAttribute('aria-label', `${bar.title}. ${text('move-hint', 'Arrow keys move time or person; Shift + arrows resize; Enter edits; Delete removes.')}`);
                        if (!hasDeparted(slot)) for (const edge of ['start', 'end']) {
                            if ((edge === 'start' && left < from) || (edge === 'end' && right > until)) continue;
                            const handle = document.createElement('span'); handle.className = 'ac-gantt-resize'; handle.dataset.resize = edge;
                            handle.title = text(`resize-${edge}`, edge === 'start' ? 'Adjust start' : 'Adjust end'); handle.setAttribute('aria-hidden', 'true'); bar.append(handle);
                        }
                    }
                    rowLane.append(bar);
                }
            }
        }
        const handovers = get('oncall-handover-lane'); handovers.replaceChildren();
        state.slots.forEach((first, index) => {
            if (!valid(first) || hasDeparted(first)) return;
            const boundary = timeOf(first, 'end', zone);
            if (boundary <= from || boundary >= until) return;
            const next = state.slots.map((slot, other) => ({ slot, other })).filter(item => item.other !== index && valid(item.slot) && timeOf(item.slot, 'start', zone) === boundary);
            if (next.length !== 1 || hasDeparted(next[0].slot) || state.slots.filter(slot => timeOf(slot, 'end', zone) === boundary).length !== 1) return;
            const marker = document.createElement('button'); marker.type = 'button'; marker.className = 'ac-gantt-handover'; marker.dataset.first = String(index); marker.dataset.second = String(next[0].other);
            marker.style.left = `${(boundary - from) / duration * 100}%`;
            marker.title = `${text('handover', 'Adjust handover')} · ${localTime(boundary, zone)} · ${zone}`; marker.setAttribute('aria-label', marker.title); handovers.append(marker);
        });
        chart.dispatchEvent(new Event('oncall-layout'));
        chart.querySelectorAll('.ac-gantt-lane').forEach(pack);
        if (focusIndex != null) findBar(Number(focusIndex), focusPerson)?.focus({ preventScroll: true });
    }
    function findBar(index, person) {
        return [...chart.querySelectorAll('[data-slot-index]')].find(bar => Number(bar.dataset.slotIndex) === index && (person == null || bar.closest('[data-user-id]').dataset.userId === person));
    }
    async function preview() {
        const revision = ++sequence;
        try {
            const data = await request(root.dataset.previewUrl, payload());
            if (revision !== sequence || gesture) return;
            list('oncall-schedule-errors', []);
            if (!state.ranges.length) draw(data.timelineWarnings);
        } catch (failure) { if (revision === sequence) error(failure.message); }
    }
    function select(index, person) {
        selected = { index, person };
        get('oncall-move-member').value = activeMember(person) ? person : members.find(member => !member.departed)?.id || '';
        get('oncall-move-start').value = state.slots[index].startLocal;
        chart.querySelectorAll('[data-slot-index]').forEach(bar => {
            const chosen = Number(bar.dataset.slotIndex) === index && bar.closest('[data-user-id]').dataset.userId === person;
            bar.classList.toggle('ac-gantt-selected', chosen); bar.setAttribute('aria-pressed', String(chosen));
        });
    }
    function openEditor(index, person) {
        select(index, person); editing = { index, person, slot: structuredClone(state.slots[index]) };
        get('oncall-dialog-start').value = editing.slot.startLocal; get('oncall-dialog-end').value = editing.slot.endLocal;
        list('oncall-dialog-errors', []);
        memberInputs(get('oncall-dialog-members'), editing.slot, (id, checked) => {
            editing.slot.userIds = checked ? [...new Set([...editing.slot.userIds, id])] : editing.slot.userIds.filter(value => value !== id);
        });
        dialog.showModal(); get('oncall-dialog-start').focus();
    }
    function closeEditor() { dialog.close(); if (selected) findBar(selected.index, selected.person)?.focus({ preventScroll: true }); editing = null; }
    function canChangeTime(slot) {
        if (!hasDeparted(slot)) return true;
        error(text('departed-edit', 'Remove departed members before changing shift times.')); return false;
    }
    function pointTime(event, lane, free = event.altKey) {
        const rect = lane.getBoundingClientRect(), before = Number(chart.dataset.beforeHours || 0) * hour, after = Number(chart.dataset.afterHours || 0) * hour;
        return snapTime(from - before + (event.clientX - rect.left) / rect.width * (duration + before + after), zone, free);
    }
    function targetAt(event) {
        const element = document.elementFromPoint(event.clientX, event.clientY);
        if (!element || element.closest('[data-readonly]')) return null;
        const lane = element.closest('.ac-gantt-lane'), row = lane?.closest('[data-user-id]');
        const time = lane ? pointTime(event, lane, true) : NaN;
        return row && time >= from && (time < until || (time === until && gesture?.mode === 'create')) && activeMember(row.dataset.userId) ? { lane, row, bar: element.closest('[data-slot-index]') } : null;
    }
    function weekTarget(event) {
        const element = document.elementFromPoint(event.clientX, event.clientY);
        const row = element?.closest('[data-user-id]');
        return element && !element.closest('[data-readonly]') && row?.dataset.userId && activeMember(row.dataset.userId) ? row.dataset.userId : null;
    }
    function selectWeek(person) {
        weekSelection = weekSelection === person ? null : person;
        chart.querySelectorAll('[data-week-user]').forEach(button => button.setAttribute('aria-pressed', String(button.dataset.weekUser === weekSelection)));
    }
    function cancelGesture() {
        if (!gesture) return;
        clearTimeout(gesture.hold);
        state = gesture.before;
        gesture = null; get('oncall-drag-tooltip').hidden = true; chart.classList.remove('ac-gantt-dragging');
        draw();
    }
    chart.classList.add('ac-gantt-editable');
    chart.addEventListener('pointerdown', event => {
        if (busy || event.button !== 0 || gesture || event.target.closest('[data-readonly]')) return;
        const bar = event.target.closest('[data-slot-index]'), handover = event.target.closest('[data-first]'), week = event.target.closest('[data-week-user]');
        const target = targetAt(event);
        if (!bar && !handover && !target && !week) return;
        const index = bar ? Number(bar.dataset.slotIndex) : handover ? Number(handover.dataset.first) : -1;
        const mode = week ? 'week' : handover ? 'handover' : event.target.dataset.resize || (bar ? 'move' : 'create');
        if (index >= 0 && mode !== 'move' && !canChangeTime(state.slots[index])) return;
        const lane = (bar || handover)?.closest('.ac-gantt-lane') || week?.closest('[data-user-id]').querySelector('.ac-gantt-lane') || target.lane;
        const person = week?.dataset.weekUser ?? bar?.closest('[data-user-id]').dataset.userId ?? target?.row.dataset.userId ?? '';
        if (bar) select(index, person);
        gesture = { index, second: handover ? Number(handover.dataset.second) : null, mode, person, lane, x: event.clientX, y: event.clientY, origin: pointTime(event, lane, true), before: structuredClone(state), pointerId: event.pointerId, ready: event.pointerType !== 'touch', moved: false };
        if (!gesture.ready) gesture.hold = setTimeout(() => { if (gesture) { gesture.ready = true; chart.classList.add('ac-gantt-dragging'); } }, 400);
        chart.setPointerCapture(event.pointerId);
        if (event.pointerType !== 'touch') event.preventDefault();
    });
    chart.addEventListener('pointermove', event => {
        const drag = gesture;
        if (!drag || drag.pointerId !== event.pointerId) return;
        const distance = Math.hypot(event.clientX - drag.x, event.clientY - drag.y);
        if (!drag.ready) { if (distance > 8) cancelGesture(); return; }
        if (distance < 5 && !drag.moved) return;
        event.preventDefault(); drag.moved = true;
        sequence++; clearTimeout(timer);
        const rect = chart.getBoundingClientRect();
        if (event.clientX > rect.right - 28) chart.scrollLeft += 20;
        else if (event.clientX < rect.left + 28) chart.scrollLeft -= 20;
        const target = targetAt(event), time = pointTime(event, drag.lane);
        state = structuredClone(drag.before);
        let slot = state.slots[drag.index];
        if (drag.mode === 'week') {
            const person = weekTarget(event);
            if (person && person !== drag.person) swapPeople(state.slots, drag.person, person);
            const tip = get('oncall-drag-tooltip'); tip.hidden = false; tip.style.left = `${Math.max(0, event.clientX - 100)}px`; tip.style.top = `${Math.max(0, event.clientY - 70)}px`;
            tip.textContent = `${members.find(member => member.id === drag.person)?.name} ↔ ${members.find(member => member.id === person)?.name || '—'}`;
            draw(); return;
        }
        if (drag.mode === 'create') {
            if (!target || time < from || time > until) return;
            const left = Math.max(from, Math.min(snapTime(drag.origin, zone, event.altKey), time)), right = Math.min(until, Math.max(snapTime(drag.origin, zone, event.altKey), time));
            slot = { userIds: drag.person ? [drag.person] : [] };
            setTime(slot, 'start', left, zone); setTime(slot, 'end', Math.max(left + minute, right), zone); state.slots.push(slot);
        } else if (drag.mode === 'move') {
            if (!target) { draw(); return; }
            const originalSlot = drag.before.slots[drag.index];
            const targetIndex = target.bar ? Number(target.bar.dataset.slotIndex) : -1;
            if (targetIndex >= 0 && targetIndex !== drag.index && !hasDeparted(slot) && !hasDeparted(state.slots[targetIndex]) && target.row.dataset.userId !== drag.person) {
                const other = state.slots[targetIndex];
                if (!slot.userIds.includes(target.row.dataset.userId) && !other.userIds.includes(drag.person)) {
                    movePerson(slot, drag.person, target.row.dataset.userId); movePerson(other, target.row.dataset.userId, drag.person);
                }
            } else {
                const delta = Math.abs(event.clientX - drag.x) < 5 ? 0 : snapTime(timeOf(originalSlot, 'start', zone) + pointTime(event, drag.lane, true) - drag.origin, zone, event.altKey) - timeOf(originalSlot, 'start', zone);
                if (delta !== 0 && hasDeparted(slot)) { draw(); return; }
                shiftTimes(slot, delta, zone); movePerson(slot, drag.person, target.row.dataset.userId);
            }
        } else if (drag.mode === 'handover') {
            const second = state.slots[drag.second];
            const boundary = Math.min(timeOf(second, 'end', zone) - minute, Math.max(timeOf(slot, 'start', zone) + minute, time));
            setTime(slot, 'end', boundary, zone); setTime(second, 'start', boundary, zone);
        } else {
            const boundary = drag.mode === 'start' ? Math.min(time, timeOf(slot, 'end', zone) - minute) : Math.max(time, timeOf(slot, 'start', zone) + minute);
            setTime(slot, drag.mode, boundary, zone);
        }
        const tip = get('oncall-drag-tooltip'); tip.hidden = false; tip.style.left = `${Math.max(0, event.clientX - 100)}px`; tip.style.top = `${Math.max(0, event.clientY - 70)}px`;
        tip.textContent = `${slot.startLocal} → ${slot.endLocal} · ${zone}`;
        chart.classList.add('ac-gantt-dragging'); draw();
    });
    chart.addEventListener('pointerup', event => {
        const drag = gesture;
        if (!drag || drag.pointerId !== event.pointerId) return;
        clearTimeout(drag.hold);
        const target = targetAt(event);
        const result = structuredClone(state);
        state = drag.before; gesture = null;
        get('oncall-drag-tooltip').hidden = true; chart.classList.remove('ac-gantt-dragging');
        if (chart.hasPointerCapture(event.pointerId)) chart.releasePointerCapture(event.pointerId);
        if (!drag.moved) {
            if (weekSelection && weekTarget(event)) {
                commit(() => swapPeople(state.slots, weekSelection, weekTarget(event))); selectWeek(weekSelection); suppressClick = true;
            } else if (drag.mode === 'week') { selectWeek(drag.person); suppressClick = true; }
            else if (drag.index >= 0 && drag.mode === 'move') { suppressClick = true; openEditor(drag.index, drag.person); }
            else if (drag.mode === 'create' && selected && target) {
                const slot = state.slots[selected.index], time = pointTime(event, target.lane);
                if (slot && time >= from && time < until && canChangeTime(slot)) {
                    commit(() => { shiftTimes(slot, time - timeOf(slot, 'start', zone), zone); movePerson(slot, selected.person, target.row.dataset.userId); });
                    select(selected.index, target.row.dataset.userId);
                }
            }
            return;
        }
        suppressClick = true;
        if (((drag.mode === 'move' || drag.mode === 'create') && !target) || (drag.mode === 'week' && !weekTarget(event))) { draw(); return; }
        commit(() => { state = result; });
        if (selected) findBar(selected.index)?.focus({ preventScroll: true });
    });
    chart.addEventListener('pointercancel', cancelGesture);
    chart.addEventListener('lostpointercapture', cancelGesture);
    chart.addEventListener('oncall-edge-change', cancelGesture);
    chart.addEventListener('click', event => {
        if (suppressClick && event.detail !== 0) { suppressClick = false; return; }
        suppressClick = false;
        if (busy || event.target.closest('[data-readonly]')) return;
        const week = event.target.closest('[data-week-user]'), row = event.target.closest('[data-user-id]');
        if (weekSelection && row?.dataset.userId && activeMember(row.dataset.userId)) {
            commit(() => swapPeople(state.slots, weekSelection, row.dataset.userId)); selectWeek(weekSelection); return;
        }
        if (week) { selectWeek(week.dataset.weekUser); return; }
        const bar = event.target.closest('[data-slot-index]');
        if (bar && !event.target.dataset.resize) openEditor(Number(bar.dataset.slotIndex), bar.closest('[data-user-id]').dataset.userId);
    });
    chart.addEventListener('keydown', event => {
        if (busy) return;
        const bar = event.target.closest('[data-slot-index]'), handover = event.target.closest('[data-first]');
        if (!bar && !handover) return;
        const index = Number((bar || handover).dataset[bar ? 'slotIndex' : 'first']), slot = state.slots[index];
        const person = bar?.closest('[data-user-id]').dataset.userId;
        if (bar) select(index, person);
        if (event.key === 'Enter' && bar) { event.preventDefault(); openEditor(index, person); return; }
        if (event.key === 'Delete' && bar) { event.preventDefault(); commit(() => state.slots.splice(index, 1)); selected = null; chart.focus(); return; }
        if (!['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown'].includes(event.key)) return;
        event.preventDefault();
        const delta = (event.altKey ? 1 : 30) * minute * (['ArrowLeft', 'ArrowUp'].includes(event.key) ? -1 : 1);
        if (handover) {
            const second = state.slots[Number(handover.dataset.second)], boundary = timeOf(slot, 'end', zone) + delta;
            if (boundary > timeOf(slot, 'start', zone) && boundary < timeOf(second, 'end', zone)) commit(() => { setTime(slot, 'end', boundary, zone); setTime(second, 'start', boundary, zone); });
            chart.querySelector(`[data-first="${index}"]`)?.focus(); return;
        }
        if (!event.shiftKey && ['ArrowUp', 'ArrowDown'].includes(event.key)) {
            const people = ['', ...members.filter(member => !member.departed).map(member => member.id)];
            const target = people[people.indexOf(person) + (event.key === 'ArrowUp' ? -1 : 1)];
            if (target != null) { commit(() => movePerson(slot, person, target)); select(index, target); findBar(index, target)?.focus(); }
        } else if (canChangeTime(slot)) {
            const edge = ['ArrowUp', 'ArrowDown'].includes(event.key) ? 'start' : 'end';
            if (event.shiftKey) {
                const candidate = structuredClone(slot); setTime(candidate, edge, timeOf(slot, edge, zone) + delta, zone);
                if (valid(candidate)) commit(() => { state.slots[index] = candidate; });
            } else commit(() => shiftTimes(slot, delta, zone));
        }
    });
    document.addEventListener('keydown', event => {
        if (event.key === 'Escape') { cancelGesture(); selectWeek(weekSelection); selected = null; chart.querySelectorAll('.ac-gantt-selected').forEach(bar => { bar.classList.remove('ac-gantt-selected'); bar.setAttribute('aria-pressed', 'false'); }); }
        if (event.target.closest('input, textarea, select, dialog') || busy) return;
        if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'z') { event.preventDefault(); travel(event.shiftKey ? redo : undo, event.shiftKey ? undo : redo); }
    });
    function travel(source, destination) {
        if (!source.length || busy) return;
        cancelGesture(); destination.push(structuredClone(state)); state = source.pop(); selected = null; changed();
    }
    async function operation(action) {
        if (busy) return;
        cancelGesture(); busy = true; sequence++; clearTimeout(timer); root.inert = true; chart.inert = true;
        try { await action(); } catch (failure) { error(failure.message); }
        finally { busy = false; root.inert = false; chart.inert = false; }
    }
    function stage(range) {
        undo.push(structuredClone(state)); redo.length = 0;
        if (range.start === from && range.end === until) state.slots = prepare(range.slots);
        else { state.ranges = state.ranges.filter(item => item.start !== range.start || item.end !== range.end); state.ranges.push(range); }
        changed();
        const toggle = document.querySelector('[data-oncall-edge="next"]');
        if (toggle && range.start >= until && toggle.getAttribute('aria-expanded') !== 'true') toggle.click();
    }
    async function range(generate) {
        cancelPending();
        const revision = sequence;
        const body = { groupId: root.dataset.groupId, version: root.dataset.version, startLocal: get('oncall-range-start').value, endLocal: get('oncall-range-end').value, slots: [] };
        if (!(wallTime(body.endLocal) > wallTime(body.startLocal))) throw new Error(text('invalid-range', 'End must be after start.'));
        if (!generate) {
            try { body.slots = copyRange(state.slots, localTime(start, zone), localTime(end, zone), body.startLocal, body.endLocal, members); }
            catch (failure) { throw new Error(failure.message === 'departed' ? text('cannot-copy-departed', 'Cannot copy assignments for departed team members.') : text('invalid-range', 'End must be after start.')); }
        }
        const data = await request(`${root.dataset.previewUrl}?generate=${generate}`, body);
        if (revision !== sequence) return;
        const destination = { start: Date.parse(data.startUtc), end: Date.parse(data.endUtc), slots: data.slots };
        const sameRange = destination.start === from && destination.end === until;
        if ((!sameRange && destination.start < until && destination.end > from) || state.ranges.some(item => item.start < destination.end && item.end > destination.start && !(item.start === destination.start && item.end === destination.end))) throw new Error(text('copy-overlap', 'The destination must not overlap the visible range or another copied range.'));
        list('oncall-schedule-warnings', data.warnings);
        const replacing = data.replacedSlots.length || sameRange || state.ranges.some(item => item.start === destination.start && item.end === destination.end);
        if (replacing) {
            list('oncall-replaced-shifts', (data.replacedSlots.length ? data.replacedSlots : sameRange ? normalized(state.slots) : state.ranges.find(item => item.start === destination.start).slots).map(slot => `${localTime(slot.startsAtUtc, zone)} → ${localTime(slot.endsAtUtc, zone)} · ${slot.userIds.map(id => members.find(member => member.id === id)?.name || id).join(', ') || text('unassigned', 'Unassigned')}`));
            pending = destination; get('oncall-replace-confirm').hidden = false;
        } else stage(destination);
    }
    async function save() {
        const body = payload();
        const all = normalized(state.slots).concat(state.ranges.flatMap(range => range.slots));
        const low = Math.min(from, ...state.ranges.map(range => range.start), ...all.map(slot => Date.parse(slot.startsAtUtc)));
        const high = Math.max(until, ...state.ranges.map(range => range.end), ...all.map(slot => Date.parse(slot.endsAtUtc)));
        if (low !== from || high !== until || state.ranges.length) {
            const query = new URLSearchParams({ id: root.dataset.groupId, startUtc: new Date(low).toISOString(), endUtc: new Date(high).toISOString() });
            const saved = await request(`${root.dataset.getUrl}?${query}`);
            if (saved.version !== root.dataset.version) throw new Error(text('schedule-save-failed', 'Unable to save schedule. Refresh and try again.'));
            const ranges = [{ start: from, end: until }, ...state.ranges];
            const preserved = (saved.saved ? saved.slots : []).filter(slot => !ranges.some(range => Date.parse(slot.startsAtUtc) < range.end && Date.parse(slot.endsAtUtc) > range.start) && !all.some(item => item.id && item.id === slot.id));
            body.startUtc = new Date(low).toISOString(); body.endUtc = new Date(high).toISOString();
            body.slots.push(...state.ranges.flatMap(range => range.slots), ...preserved);
        }
        await request(root.dataset.saveUrl, body);
        dirty = false; window.location.reload();
    }
    get('oncall-dialog-apply').addEventListener('click', () => {
        const slot = structuredClone(editing.slot);
        slot.startLocal = get('oncall-dialog-start').value; slot.endLocal = get('oncall-dialog-end').value;
        if (!valid(slot)) { list('oncall-dialog-errors', [text('invalid-range', 'End must be after start.'), text('invalid-local', 'Invalid team local time.')]); return; }
        if (hasDeparted(slot) && (slot.startLocal !== state.slots[editing.index].startLocal || slot.endLocal !== state.slots[editing.index].endLocal)) { list('oncall-dialog-errors', [text('departed-edit', 'Remove departed members before changing shift times.')]); return; }
        commit(() => { state.slots[editing.index] = slot; }); closeEditor();
    });
    get('oncall-dialog-delete').addEventListener('click', () => { commit(() => state.slots.splice(editing.index, 1)); selected = null; closeEditor(); chart.focus(); });
    get('oncall-dialog-cancel').addEventListener('click', closeEditor);
    dialog.addEventListener('cancel', event => { event.preventDefault(); closeEditor(); });
    get('oncall-add-shift').addEventListener('click', () => commit(() => state.slots.push({ startLocal: localTime(start, zone), endLocal: localTime(end, zone), userIds: [] })));
    get('oncall-undo-schedule').addEventListener('click', () => travel(undo, redo));
    get('oncall-redo-schedule').addEventListener('click', () => travel(redo, undo));
    get('oncall-reset-schedule').addEventListener('click', () => commit(() => { state = structuredClone(original); selected = null; }));
    get('oncall-swap-week').addEventListener('click', () => {
        const first = get('oncall-swap-first').value, second = get('oncall-swap-second').value;
        if (!first || !second || first === second || !activeMember(first) || !activeMember(second)) { error(text('swap-invalid', 'Choose two different current team members.')); return; }
        commit(() => swapPeople(state.slots, first, second));
    });
    get('oncall-move-selected').addEventListener('click', () => {
        if (!selected || !state.slots[selected.index]) { error(text('select-shift', 'Select a shift first.')); return; }
        const slot = state.slots[selected.index], target = get('oncall-move-member').value, time = utcForLocal(get('oncall-move-start').value, zone);
        if (!Number.isFinite(time)) { error(text('invalid-local', 'Invalid team local time.')); return; }
        if (!activeMember(target) || !canChangeTime(slot)) return;
        commit(() => { shiftTimes(slot, time - timeOf(slot, 'start', zone), zone); movePerson(slot, selected.person, target); }); select(selected.index, target);
    });
    get('oncall-save-schedule').addEventListener('click', () => operation(save));
    get('oncall-copy-range').addEventListener('click', () => operation(() => range(false)));
    get('oncall-generate')?.addEventListener('click', () => operation(() => range(true)));
    get('oncall-replace-cancel').addEventListener('click', cancelPending);
    get('oncall-replace-apply').addEventListener('click', () => { if (pending && !busy) stage(pending); });
    for (const id of ['oncall-range-start', 'oncall-range-end']) get(id).addEventListener('input', () => { sequence++; cancelPending(); });
    get('oncall-range-start').value = localTime(end, zone);
    get('oncall-range-end').value = wallString(wallTime(localTime(end, zone)) + 7 * 86400000);
    window.addEventListener('beforeunload', event => { if (dirty) { event.preventDefault(); event.returnValue = ''; } });
    chart.addEventListener('oncall-packed', () => chart.querySelectorAll('.ac-gantt-lane').forEach(pack));
    renderRows(); draw();
    get('oncall-undo-schedule').disabled = true; get('oncall-redo-schedule').disabled = true;
    preview();
})();
