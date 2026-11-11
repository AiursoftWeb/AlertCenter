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
                if (copyEnd > copyStart) copies.push({ startLocal: wallString(copyStart), endLocal: wallString(copyEnd), userIds: [...slot.userIds], source: 0 });
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
    function markManual(slot) {
        slot.source = 0;
        slot.rotationPosition = null;
    }
    function setTime(slot, edge, utc, zone) {
        if (timeOf(slot, edge, zone) !== utc) markManual(slot);
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
    function samePeople(first, second) {
        return JSON.stringify([...new Set(first)].sort()) === JSON.stringify([...new Set(second)].sort());
    }
    function movePerson(slot, from, to) {
        if (from === to) return;
        const people = to === '' ? [] : [...new Set(slot.userIds.filter(id => id !== from).concat(to))];
        if (!samePeople(slot.userIds, people)) markManual(slot);
        slot.userIds = people;
    }
    function swapPeople(slots, first, second) {
        for (const slot of slots) {
            if (slot.userIds.includes(first) !== slot.userIds.includes(second)) markManual(slot);
            slot.userIds = slot.userIds.map(id => id === first ? second : id === second ? first : id);
        }
    }
    const peopleOf = slot => slot.userIds.length ? slot.userIds : [''];
    const sharesPerson = (first, second) => peopleOf(first).some(id => peopleOf(second).includes(id));
    function overlappingSlots(slots, zone) {
        const conflicts = new Set();
        const ordered = slots.map(slot => ({ slot, left: timeOf(slot, 'start', zone), right: timeOf(slot, 'end', zone) }))
            .filter(item => item.right > item.left).sort((a, b) => a.left - b.left);
        for (let i = 0; i < ordered.length; i++) {
            for (let j = i + 1; j < ordered.length && ordered[j].left < ordered[i].right; j++) {
                if (sharesPerson(ordered[i].slot, ordered[j].slot)) { conflicts.add(ordered[i].slot); conflicts.add(ordered[j].slot); }
            }
        }
        return conflicts;
    }
    function freeBounds(slots, slot, zone, excluded = []) {
        const left = timeOf(slot, 'start', zone), right = timeOf(slot, 'end', zone);
        let low = -Infinity, high = Infinity;
        for (const other of slots) {
            if (other === slot || excluded.includes(other) || !sharesPerson(slot, other)) continue;
            const start = timeOf(other, 'start', zone), end = timeOf(other, 'end', zone);
            if (end <= left) low = Math.max(low, end);
            if (start >= right) high = Math.min(high, start);
        }
        return { low, high };
    }
    function clampMove(slots, slot, delta, zone) {
        const { low, high } = freeBounds(slots, slot, zone);
        return Math.max(low - timeOf(slot, 'start', zone), Math.min(high - timeOf(slot, 'end', zone), delta));
    }
    function clampEdge(slots, slot, edge, time, zone, excluded = []) {
        const { low, high } = freeBounds(slots, slot, zone, excluded);
        return edge === 'start' ? Math.max(low, Math.min(timeOf(slot, 'end', zone) - minute, time)) : Math.min(high, Math.max(timeOf(slot, 'start', zone) + minute, time));
    }
    function mergeOverlaps(slots, zone) {
        const affected = slots.filter(slot => slots.some(other => other !== slot && sharesPerson(slot, other) && timeOf(slot, 'start', zone) <= timeOf(other, 'end', zone) && timeOf(other, 'start', zone) <= timeOf(slot, 'end', zone)));
        const merged = structuredClone(slots.filter(slot => !affected.includes(slot)));
        // 按成员合并，避免多人班段的部分重叠改变其他成员的值班时长。
        for (const person of new Set(affected.flatMap(peopleOf))) {
            const ordered = affected.filter(slot => peopleOf(slot).includes(person)).sort((a, b) => timeOf(a, 'start', zone) - timeOf(b, 'start', zone));
            let current = null;
            for (const slot of ordered) {
                if (current && timeOf(slot, 'start', zone) <= timeOf(current, 'end', zone)) {
                    setTime(current, 'end', Math.max(timeOf(current, 'end', zone), timeOf(slot, 'end', zone)), zone);
                } else {
                    current = { ...structuredClone(slot), userIds: person ? [person] : [] };
                    delete current.id; markManual(current); merged.push(current);
                }
            }
        }
        return merged;
    }
    function snapCandidates(slots, handovers, start, end, zone, now) {
        const candidates = slots.flatMap(slot => [timeOf(slot, 'start', zone), timeOf(slot, 'end', zone)]).concat(handovers.map(value => new Date(value).getTime()), Math.floor(now / minute) * minute);
        for (let day = wallTime(`${localTime(start, zone).slice(0, 10)}T00:00`); day <= wallTime(localTime(end, zone)); day += 24 * hour) {
            candidates.push(utcForLocal(wallString(day), zone));
        }
        return [...new Set(candidates.filter(Number.isFinite))];
    }
    function snapBoundary(time, zone, candidates, tolerance, free = false, length = 0) {
        if (free) return { time: snapTime(time, zone, true), guide: null };
        let best = null, distance = Infinity, snapped = time;
        for (const candidate of candidates) for (const offset of length ? [0, length] : [0]) {
            const difference = Math.abs(time + offset - candidate);
            if (difference <= tolerance && difference < distance) { distance = difference; best = candidate; snapped = candidate - offset; }
        }
        return best == null ? { time: snapTime(time, zone), guide: null } : { time: snapped, guide: best };
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
    function warningSegments(warnings, start, end) {
        const events = new Map();
        const add = (time, message, delta) => {
            if (!events.has(time)) events.set(time, new Map());
            const changes = events.get(time);
            changes.set(message, (changes.get(message) || 0) + delta);
        };
        for (const warning of warnings) {
            const left = Math.max(start, new Date(warning.startsAtUtc).getTime()), right = Math.min(end, new Date(warning.endsAtUtc).getTime());
            if (!(right > left)) continue;
            add(left, warning.message, 1); add(right, warning.message, -1);
        }
        const active = new Map(), segments = [];
        let previous = start;
        for (const [time, changes] of [...events].sort((a, b) => a[0] - b[0])) {
            if (time > previous && active.size) {
                const messages = [...active.keys()].sort(), last = segments.at(-1);
                if (last?.right === previous && JSON.stringify(last.messages) === JSON.stringify(messages)) last.right = time;
                else segments.push({ left: previous, right: time, messages });
            }
            for (const [message, delta] of changes) {
                const count = (active.get(message) || 0) + delta;
                if (count) active.set(message, count);
                else active.delete(message);
            }
            previous = time;
        }
        return segments;
    }
    if (typeof module !== 'undefined') module.exports = { localTime, copyRange, hoursFor, utcForLocal, timeOf, snapTime, movePerson, swapPeople, reminders, warningSegments, overlappingSlots, freeBounds, clampMove, clampEdge, mergeOverlaps, snapCandidates, snapBoundary };
    if (typeof document === 'undefined') return;
    const root = document.getElementById('oncall-schedule');
    if (!root) return;
    const get = id => document.getElementById(id);
    const text = (key, fallback) => root.querySelector(`[data-schedule-text="${key}"]`)?.textContent || getLocalizedText(key, fallback);
    const members = JSON.parse(root.dataset.members), context = JSON.parse(root.dataset.context || '[]');
    const zone = root.dataset.zone, start = root.dataset.start, end = root.dataset.end;
    const from = Date.parse(start), until = Date.parse(end), duration = until - from;
    const chart = get('oncall-gantt'), dialog = get('oncall-shift-dialog');
    const prepare = items => items.map(slot => ({ ...slot, source: slot.source ?? 0, userIds: [...slot.userIds], startLocal: localTime(slot.startsAtUtc, zone), endLocal: localTime(slot.endsAtUtc, zone) }));
    const original = { slots: prepare(JSON.parse(root.dataset.slots)), ranges: [], clearRanges: [] };
    let state = structuredClone(original), dirty = false, busy = false, sequence = 0, timer, pending = null, historyPending = null, selected = null, weekSelection = null, editing = null, gesture = null, suppressClick = false, clearEndEdited = false;
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
    const overlapMessage = () => text('same-person-overlap', 'Shifts for the same person or unassigned row cannot overlap. Merge or adjust them before saving.');
    function draftSlots(candidate = state) { return candidate.slots.concat(candidate.ranges.flatMap(range => range.slots)); }
    function teamSlots(candidate = state) {
        const slots = draftSlots(candidate), ranges = [{ start: from, end: until }, ...candidate.ranges];
        return slots.concat(context.filter(slot => !slot.external && !ranges.some(range => Date.parse(slot.startsAtUtc) < range.end && Date.parse(slot.endsAtUtc) > range.start) && !slots.some(item => item.id && item.id === slot.id)));
    }
    const conflicts = (candidate = state) => overlappingSlots(teamSlots(candidate), zone);
    function submitted(items) {
        return items.map(slot => {
            const copy = structuredClone(slot);
            copy.source ??= 0;
            if (copy.startsAtUtc && copy.startLocal === localTime(copy.startsAtUtc, zone)) delete copy.startLocal;
            if (copy.endsAtUtc && copy.endLocal === localTime(copy.endsAtUtc, zone)) delete copy.endLocal;
            return copy;
        });
    }
    function payload(extraRange) {
        const slots = state.slots.concat(state.ranges.flatMap(range => range.slots));
        const bounds = normalized(slots);
        const low = Math.min(from, extraRange ? Date.parse(extraRange.startUtc) : Infinity, ...state.ranges.map(range => range.start), ...state.clearRanges.map(range => Date.parse(range.startUtc)), ...bounds.map(slot => Date.parse(slot.startsAtUtc)));
        const high = Math.max(until, extraRange ? Date.parse(extraRange.endUtc) : -Infinity, ...state.ranges.map(range => range.end), ...state.clearRanges.map(range => Date.parse(range.endUtc)), ...bounds.map(slot => Date.parse(slot.endsAtUtc)));
        if (high - low > Number(root.dataset.maxRangeDays) * 24 * hour) {
            throw new Error(text('range-limit', 'The schedule range must not exceed {0} days and must be within 10 years of now.').replace('{0}', root.dataset.maxRangeDays));
        }
        return { groupId: root.dataset.groupId, version: root.dataset.version, startUtc: new Date(low).toISOString(), endUtc: new Date(high).toISOString(), slots: submitted(slots), clearRanges: structuredClone(state.clearRanges) };
    }
    async function request(url, body) {
        const response = await fetch(url, body ? { method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json', RequestVerificationToken: root.querySelector('[name="__RequestVerificationToken"]').value }, body: JSON.stringify(body) } : { credentials: 'same-origin' });
        const data = await response.json();
        if (!response.ok) throw new Error((data.errors || [text('schedule-save-failed', 'Unable to save schedule. Refresh and try again.')]).join('\n'));
        return data;
    }
    function cancelPending() {
        pending = null; historyPending = null;
        get('oncall-replace-confirm').hidden = true;
        get('oncall-clear-history-confirm').hidden = true;
    }
    function clearDefaults() {
        if (clearEndEdited) return;
        const ends = normalized(state.slots.concat(state.ranges.flatMap(range => range.slots))).map(slot => Date.parse(slot.endsAtUtc));
        const savedEnd = Date.parse(root.dataset.lastShiftEnd);
        const latest = Math.max(Number.isFinite(savedEnd) ? savedEnd : -Infinity, ...ends, ...state.ranges.map(range => range.end), ...state.clearRanges.map(range => Date.parse(range.endUtc)));
        get('oncall-clear-end').value = Number.isFinite(latest) ? localTime(Math.ceil(latest / minute) * minute, zone) : '';
    }
    function describeSlots(slots) {
        return slots.map(slot => `${localTime(slot.startsAtUtc, zone)} → ${localTime(slot.endsAtUtc, zone)} · ${slot.userIds.map(id => members.find(member => member.id === id)?.name || id).join(', ') || text('unassigned', 'Unassigned')}`);
    }
    function confirmReplacement(candidate, slots, regenerate = false) {
        pending = candidate;
        list('oncall-replaced-shifts', describeSlots(slots));
        get('oncall-replace-regenerate-hint').hidden = !regenerate;
        get('oncall-replace-confirm').hidden = false;
    }
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
        list('oncall-copy-drafts', state.ranges.map(range => `${text('draft-range', 'Draft range')}: ${localTime(range.start, zone)} → ${localTime(range.end, zone)} · ${zone}`)
            .concat(state.clearRanges.map(range => `${text('cleared-range', 'Cleared draft range')}: ${localTime(range.startUtc, zone)} → ${localTime(range.endUtc, zone)} · ${zone}${range.autoExtendAction ? ` · ${text(`clear-${range.autoExtendAction}`, range.autoExtendAction === 'disable' ? 'Disable automatic extension when saving' : 'Regenerate cleared gaps from the template when saving')}` : ''}`)));
        get('oncall-clear-auto-action').value = state.clearRanges.at(-1)?.autoExtendAction || '';
        clearDefaults();
        if (rows) renderRows();
        draw();
        clearTimeout(timer);
        timer = setTimeout(preview, 250);
    }
    function commit(action, rows = true, allowExisting = false, errorTarget = 'oncall-schedule-errors') {
        if (busy) return false;
        const before = structuredClone(state);
        action();
        if (!allowExisting && conflicts().size) {
            state = before; sequence++; clearTimeout(timer); renderRows(); draw(); list(errorTarget, [overlapMessage()]); return false;
        }
        if (JSON.stringify(before) === JSON.stringify(state)) return true;
        undo.push(before); redo.length = 0;
        changed(rows); return true;
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
                input.addEventListener('input', () => commit(() => {
                    if (state.slots[index][key] !== input.value) markManual(state.slots[index]);
                    state.slots[index][key] = input.value;
                }, false));
                cell.append(input); row.append(cell);
            }
            const people = document.createElement('td');
            memberInputs(people, slot, (id, checked) => commit(() => {
                if (state.slots[index].userIds.includes(id) !== checked) markManual(state.slots[index]);
                state.slots[index].userIds = checked ? [...new Set([...state.slots[index].userIds, id])] : state.slots[index].userIds.filter(value => value !== id);
            }));
            row.append(people);
            const actions = document.createElement('td'), remove = document.createElement('button');
            remove.type = 'button'; remove.className = 'btn btn-sm btn-outline-danger'; remove.textContent = text('delete-shift', 'Delete shift');
            remove.addEventListener('click', () => commit(() => { state.slots.splice(index, 1); selected = null; }, true, true));
            actions.append(remove); row.append(actions); return row;
        }));
    }
    function position(element, left, right) {
        const before = Number(chart.dataset.beforeHours || 0) * hour, after = Number(chart.dataset.afterHours || 0) * hour;
        element.dataset.layoutLeft = String((left - from) / duration * 100);
        element.dataset.layoutWidth = String((right - left) / duration * 100);
        element.style.left = `${(left - from + before) / (duration + before + after) * 100}%`;
        element.style.width = `${(right - left) / (duration + before + after) * 100}%`;
    }
    function describe(element, summary, left, right) {
        element.dataset.summary = summary; element.dataset.start = localTime(left, zone); element.dataset.end = localTime(right, zone);
        element.title = `${summary} · ${element.dataset.start} → ${element.dataset.end} · ${zone}`;
        element.setAttribute('aria-label', element.title);
        element.setAttribute('aria-describedby', 'oncall-shift-detail');
    }
    function pack(lane) {
        if (gesture || lane.id === 'oncall-warning-lane') return;
        const tracks = [];
        const bars = [...lane.querySelectorAll('.ac-gantt-shift')].filter(bar => !bar.hidden);
        bars.sort((a, b) => parseFloat(a.dataset.layoutLeft ?? a.style.left) - parseFloat(b.dataset.layoutLeft ?? b.style.left));
        for (const bar of bars) {
            const left = parseFloat(bar.dataset.layoutLeft ?? bar.style.left), right = left + parseFloat(bar.dataset.layoutWidth ?? bar.style.width);
            let track = tracks.findIndex(value => value <= left + 0.000001);
            if (track < 0) track = tracks.length;
            tracks[track] = right;
            bar.style.top = `${track * 72 + 8}px`; bar.style.transform = 'none';
        }
        lane.style.minHeight = `${Math.max(1, tracks.length) * 72}px`;
    }
    function updateShift(bar, row, slot, editable) {
        const left = timeOf(slot, 'start', zone), right = timeOf(slot, 'end', zone);
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
    }
    function drawGesture() {
        const previous = gesture.rendered || gesture.before;
        // 保留未编辑的 DOM 和轨道，避免手势过程中命中目标随重绘改变。
        for (let index = 0; index < Math.max(previous.slots.length, state.slots.length); index++) {
            const slot = state.slots[index];
            if (JSON.stringify(slot) === JSON.stringify(previous.slots[index])) continue;
            for (const row of chart.querySelectorAll('[data-user-id]')) {
                const lane = row.querySelector('.ac-gantt-lane'), id = row.dataset.userId;
                let bar = findBar(index, id);
                if (!slot || !valid(slot) || !(slot.userIds.includes(id) || (id === '' && !slot.userIds.length))) { bar?.remove(); continue; }
                const left = Math.max(from, timeOf(slot, 'start', zone)), right = Math.min(until, timeOf(slot, 'end', zone));
                if (right <= left) { bar?.remove(); continue; }
                if (!bar) {
                    bar = document.createElement('button'); bar.type = 'button';
                    bar.className = `ac-gantt-shift ac-oncall-color-${row.dataset.color}`;
                    bar.dataset.draftShift = 'true'; bar.dataset.slotIndex = String(index);
                    bar.style.top = '8px'; bar.style.transform = 'none';
                    bar.setAttribute('aria-pressed', String(selected?.index === index && selected?.person === id));
                    lane.append(bar);
                }
                position(bar, left, right); updateShift(bar, row, slot, true);
            }
        }
        if (gesture.mode === 'handover') {
            const marker = chart.querySelector(`[data-first="${gesture.index}"]`), boundary = timeOf(state.slots[gesture.index], 'end', zone);
            if (marker) {
                position(marker, boundary, boundary); marker.style.width = '';
                marker.title = `${text('handover', 'Adjust handover')} · ${localTime(boundary, zone)} · ${zone}`;
                marker.setAttribute('aria-label', marker.title);
            }
        }
        gesture.rendered = structuredClone(state);
    }
    function draw(serverWarnings) {
        if (gesture) { drawGesture(); return; }
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
        const overlaps = conflicts();
        get('oncall-save-schedule').disabled = overlaps.size > 0;
        get('oncall-merge-overlaps').hidden = !overlaps.size;
        list('oncall-schedule-warnings', [...new Set(warnings.map(warning => warning.message).concat(overlaps.size ? [overlapMessage()] : []))]);
        const lane = get('oncall-warning-lane'); lane.replaceChildren();
        for (const warning of warningSegments(warnings, from, until)) {
            const message = warning.messages.join('\n');
            const marker = document.createElement('button'); marker.type = 'button'; marker.className = 'ac-gantt-shift ac-gantt-warning bg-warning text-dark';
            marker.dataset.readonly = 'true';
            const count = document.createElement('span'), label = document.createElement('span');
            count.className = 'badge bg-dark ac-gantt-warning-count'; count.textContent = String(warning.messages.length);
            label.className = 'ac-gantt-warning-text'; label.textContent = message;
            count.setAttribute('aria-hidden', 'true'); label.setAttribute('aria-hidden', 'true'); marker.append(count, label);
            position(marker, warning.left, warning.right); describe(marker, message, warning.left, warning.right); lane.append(marker);
        }
        for (const row of chart.querySelectorAll('[data-user-id]')) {
            const id = row.dataset.userId, rowLane = row.querySelector('.ac-gantt-lane');
            const count = resolved.filter(slot => slot.userIds.includes(id) && Date.parse(slot.startsAtUtc) < until && Date.parse(slot.endsAtUtc) > from).length;
            row.querySelector('.ac-gantt-hours').textContent = `${count} · ${hoursFor(resolved, id, start, end).toFixed(2)} ${text('oncall-hours-unit', 'hours')}`;
            rowLane.querySelectorAll('.ac-gantt-adjacent:not([data-draft-shift])').forEach(bar => {
                bar.dataset.replaced = String(state.ranges.some(range => utcForLocal(bar.dataset.start?.slice(0, 16).replace(' ', 'T'), zone) < range.end && utcForLocal(bar.dataset.end?.slice(0, 16).replace(' ', 'T'), zone) > range.start));
                const invalid = [...overlaps].some(slot => peopleOf(slot).includes(id) && localTime(timeOf(slot, 'start', zone), zone) === bar.dataset.start?.slice(0, 16).replace(' ', 'T') && localTime(timeOf(slot, 'end', zone), zone) === bar.dataset.end?.slice(0, 16).replace(' ', 'T'));
                bar.classList.toggle('ac-gantt-conflict', invalid); bar.setAttribute('aria-invalid', String(invalid));
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
                    updateShift(bar, row, slot, editable);
                    if (overlaps.has(slot)) { bar.classList.add('ac-gantt-conflict'); bar.setAttribute('aria-invalid', 'true'); bar.title += ` · ${overlapMessage()}`; }
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
            list('oncall-schedule-errors', data.errors || []);
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
    function rawPointTime(event, lane) {
        const rect = lane.getBoundingClientRect(), before = Number(chart.dataset.beforeHours || 0) * hour, after = Number(chart.dataset.afterHours || 0) * hour;
        return from - before + (event.clientX - rect.left) / rect.width * (duration + before + after);
    }
    function pointTime(event, lane, free = event.altKey) {
        return snapTime(rawPointTime(event, lane), zone, free);
    }
    function snapGesture(time, event, length = 0) {
        const drag = gesture, rect = drag.lane.getBoundingClientRect();
        const total = duration + (Number(chart.dataset.beforeHours || 0) + Number(chart.dataset.afterHours || 0)) * hour;
        const snapped = snapBoundary(time, zone, drag.candidates, total / rect.width * 10, event.altKey, length);
        drag.guide = snapped.guide;
        return snapped.time;
    }
    function showGuide() {
        const line = get('oncall-snap-line');
        line.hidden = gesture?.guide == null || gesture.invalid;
        if (!line.hidden) {
            position(line, gesture.guide, gesture.guide); line.style.width = '';
            line.querySelector('span').textContent = `${localTime(gesture.guide, zone)} · ${zone}`;
        }
    }
    function handoverTime(first, second, time) {
        const slots = teamSlots(), firstBounds = freeBounds(slots, first, zone, [second]), secondBounds = freeBounds(slots, second, zone, [first]);
        const low = Math.max(timeOf(first, 'start', zone) + minute, secondBounds.low);
        const high = Math.min(timeOf(second, 'end', zone) - minute, firstBounds.high);
        return Math.max(low, Math.min(high, time));
    }
    function finishDragStyle() {
        get('oncall-drag-tooltip').hidden = true; get('oncall-snap-line').hidden = true;
        chart.classList.remove('ac-gantt-dragging', 'ac-gantt-drop-invalid');
        chart.querySelectorAll('.ac-gantt-drop-invalid').forEach(bar => bar.classList.remove('ac-gantt-drop-invalid'));
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
        const pointerId = gesture.pointerId;
        gesture = null; finishDragStyle();
        if (chart.hasPointerCapture(pointerId)) chart.releasePointerCapture(pointerId);
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
        const origin = rawPointTime(event, lane);
        if (mode === 'create') {
            const occupied = state.slots.findIndex(slot => peopleOf(slot).includes(person) && timeOf(slot, 'start', zone) <= origin && origin < timeOf(slot, 'end', zone));
            if (occupied >= 0) { select(occupied, person); suppressClick = true; event.preventDefault(); return; }
        }
        const second = handover ? Number(handover.dataset.second) : null;
        const candidates = snapCandidates(teamSlots().filter(slot => slot !== state.slots[index] && slot !== state.slots[second]), JSON.parse(root.dataset.handovers || '[]'), from - 36 * hour, until + 36 * hour, zone, Date.now());
        gesture = { index, second, mode, person, lane, x: event.clientX, y: event.clientY, origin, candidates, before: structuredClone(state), pointerId: event.pointerId, ready: event.pointerType !== 'touch', moved: false };
        if (mode === 'create') {
            const anchor = { userIds: person ? [person] : [], startsAtUtc: new Date(origin).toISOString(), endsAtUtc: new Date(origin).toISOString() };
            gesture.bounds = freeBounds(teamSlots(), anchor, zone);
        }
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
        const target = targetAt(event), time = rawPointTime(event, drag.lane);
        state = structuredClone(drag.before); drag.invalid = false; drag.guide = null;
        chart.classList.remove('ac-gantt-drop-invalid');
        chart.querySelectorAll('.ac-gantt-drop-invalid').forEach(bar => bar.classList.remove('ac-gantt-drop-invalid'));
        let slot = state.slots[drag.index];
        if (drag.mode === 'week') {
            const person = weekTarget(event);
            if (person && person !== drag.person) swapPeople(state.slots, drag.person, person);
            drag.invalid = conflicts().size > 0;
            const tip = get('oncall-drag-tooltip'); tip.hidden = false; tip.style.left = `${Math.max(0, event.clientX - 100)}px`; tip.style.top = `${Math.max(0, event.clientY - 70)}px`;
            tip.textContent = drag.invalid ? overlapMessage() : `${members.find(member => member.id === drag.person)?.name} ↔ ${members.find(member => member.id === person)?.name || '—'}`;
            if (drag.invalid) state = structuredClone(drag.before);
            chart.classList.toggle('ac-gantt-drop-invalid', drag.invalid);
            draw(); showGuide(); return;
        }
        if (drag.mode === 'create') {
            if (!target) { draw(); showGuide(); return; }
            const low = Math.max(from, drag.bounds.low), high = Math.min(until, drag.bounds.high);
            const anchor = Math.max(low, Math.min(high, snapGesture(drag.origin, event)));
            const anchorGuide = drag.guide === anchor ? drag.guide : null;
            const boundary = Math.max(low, Math.min(high, snapGesture(time, event)));
            const left = Math.min(anchor, boundary), right = Math.max(anchor, boundary);
            if (right - left < minute) { draw(); showGuide(); return; }
            if (drag.guide !== boundary) drag.guide = anchorGuide;
            slot = { userIds: drag.person ? [drag.person] : [] };
            setTime(slot, 'start', left, zone); setTime(slot, 'end', right, zone); state.slots.push(slot);
        } else if (drag.mode === 'move') {
            if (!target) { draw(); showGuide(); return; }
            const originalSlot = drag.before.slots[drag.index];
            const targetIndex = target.bar ? Number(target.bar.dataset.slotIndex) : -1;
            if (targetIndex >= 0 && targetIndex !== drag.index && !hasDeparted(slot) && !hasDeparted(state.slots[targetIndex]) && target.row.dataset.userId !== drag.person) {
                const other = state.slots[targetIndex];
                if (!slot.userIds.includes(target.row.dataset.userId) && !other.userIds.includes(drag.person)) {
                    movePerson(slot, drag.person, target.row.dataset.userId); movePerson(other, target.row.dataset.userId, drag.person);
                }
            } else {
                const originalStart = timeOf(originalSlot, 'start', zone), length = timeOf(originalSlot, 'end', zone) - originalStart;
                let delta = Math.abs(event.clientX - drag.x) < 5 ? 0 : snapGesture(originalStart + time - drag.origin, event, length) - originalStart;
                if (delta !== 0 && hasDeparted(slot)) { draw(); showGuide(); return; }
                if (target.row.dataset.userId === drag.person) delta = clampMove(teamSlots(), slot, delta, zone);
                else {
                    const retained = slot.userIds.filter(id => id !== drag.person);
                    if (target.row.dataset.userId && retained.length) delta = clampMove(teamSlots().filter(item => item !== slot), { ...slot, userIds: retained }, delta, zone);
                }
                shiftTimes(slot, delta, zone); movePerson(slot, drag.person, target.row.dataset.userId);
                if (![timeOf(slot, 'start', zone), timeOf(slot, 'end', zone)].includes(drag.guide)) drag.guide = null;
            }
        } else if (drag.mode === 'handover') {
            const second = state.slots[drag.second];
            const boundary = handoverTime(slot, second, snapGesture(time, event));
            setTime(slot, 'end', boundary, zone); setTime(second, 'start', boundary, zone);
            if (drag.guide !== boundary) drag.guide = null;
        } else {
            const boundary = clampEdge(teamSlots(), slot, drag.mode, snapGesture(time, event), zone);
            setTime(slot, drag.mode, boundary, zone);
            if (drag.guide !== boundary) drag.guide = null;
        }
        drag.invalid = conflicts().size > 0;
        const tip = get('oncall-drag-tooltip'); tip.hidden = false; tip.style.left = `${Math.max(0, event.clientX - 100)}px`; tip.style.top = `${Math.max(0, event.clientY - 70)}px`;
        tip.textContent = drag.invalid ? text('drop-conflict', 'This move would overlap another shift for the same person. The draft was not changed.') : `${slot.startLocal} → ${slot.endLocal} · ${zone}`;
        if (drag.invalid) state = structuredClone(drag.before);
        chart.classList.toggle('ac-gantt-drop-invalid', drag.invalid);
        chart.classList.add('ac-gantt-dragging'); draw(); showGuide();
        if (drag.invalid) findBar(drag.index, drag.person)?.classList.add('ac-gantt-drop-invalid');
    });
    chart.addEventListener('pointerup', event => {
        const drag = gesture;
        if (!drag || drag.pointerId !== event.pointerId) return;
        clearTimeout(drag.hold);
        const target = targetAt(event);
        const result = structuredClone(state);
        state = drag.before; gesture = null;
        finishDragStyle();
        if (chart.hasPointerCapture(event.pointerId)) chart.releasePointerCapture(event.pointerId);
        if (drag.invalid) { draw(); error(text('drop-conflict', 'This move would overlap another shift for the same person. The draft was not changed.')); suppressClick = true; return; }
        if (!drag.moved) {
            if (weekSelection && weekTarget(event)) {
                commit(() => swapPeople(state.slots, weekSelection, weekTarget(event))); selectWeek(weekSelection); suppressClick = true;
            } else if (drag.mode === 'week') { selectWeek(drag.person); suppressClick = true; }
            else if (drag.index >= 0 && drag.mode === 'move') { suppressClick = true; openEditor(drag.index, drag.person); }
            else if (drag.mode === 'create' && selected && target) {
                const slot = state.slots[selected.index], time = pointTime(event, target.lane);
                if (slot && time >= from && time < until && canChangeTime(slot)) {
                    if (commit(() => { shiftTimes(slot, time - timeOf(slot, 'start', zone), zone); movePerson(slot, selected.person, target.row.dataset.userId); })) select(selected.index, target.row.dataset.userId);
                }
            }
            return;
        }
        suppressClick = true;
        if (((drag.mode === 'move' || drag.mode === 'create') && !target) || (drag.mode === 'week' && !weekTarget(event))) { draw(); return; }
        if (JSON.stringify(state) === JSON.stringify(result)) draw();
        else commit(() => { state = result; });
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
        if (event.key === 'Delete' && bar) { event.preventDefault(); commit(() => state.slots.splice(index, 1), true, true); selected = null; chart.focus(); return; }
        if (!['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown'].includes(event.key)) return;
        event.preventDefault();
        const delta = (event.altKey ? 1 : 30) * minute * (['ArrowLeft', 'ArrowUp'].includes(event.key) ? -1 : 1);
        if (handover) {
            const second = state.slots[Number(handover.dataset.second)], boundary = handoverTime(slot, second, timeOf(slot, 'end', zone) + delta);
            if (boundary > timeOf(slot, 'start', zone) && boundary < timeOf(second, 'end', zone)) commit(() => { setTime(slot, 'end', boundary, zone); setTime(second, 'start', boundary, zone); });
            chart.querySelector(`[data-first="${index}"]`)?.focus(); return;
        }
        if (!event.shiftKey && ['ArrowUp', 'ArrowDown'].includes(event.key)) {
            const people = ['', ...members.filter(member => !member.departed).map(member => member.id)];
            const target = people[people.indexOf(person) + (event.key === 'ArrowUp' ? -1 : 1)];
            if (target != null && commit(() => movePerson(slot, person, target))) { select(index, target); findBar(index, target)?.focus(); }
        } else if (canChangeTime(slot)) {
            const edge = ['ArrowUp', 'ArrowDown'].includes(event.key) ? 'start' : 'end';
            if (event.shiftKey) {
                const candidate = structuredClone(slot); setTime(candidate, edge, clampEdge(teamSlots(), slot, edge, timeOf(slot, edge, zone) + delta, zone), zone);
                if (valid(candidate)) commit(() => { state.slots[index] = candidate; });
            } else commit(() => shiftTimes(slot, clampMove(teamSlots(), slot, delta, zone), zone));
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
    function rangeDraft(range) {
        const candidate = structuredClone(state);
        if (range.start === from && range.end === until) candidate.slots = prepare(range.slots);
        else { candidate.ranges = candidate.ranges.filter(item => item.start !== range.start || item.end !== range.end); candidate.ranges.push(range); }
        return candidate;
    }
    function stage(range) {
        const candidate = rangeDraft(range);
        if (conflicts(candidate).size) { error(overlapMessage()); return; }
        undo.push(structuredClone(state)); redo.length = 0; state = candidate;
        changed();
        const toggle = document.querySelector('[data-oncall-edge="next"]');
        if (toggle && range.start >= until && toggle.getAttribute('aria-expanded') !== 'true') toggle.click();
    }
    async function range(generate) {
        cancelPending();
        const revision = sequence;
        const body = { groupId: root.dataset.groupId, version: root.dataset.version, startLocal: get('oncall-range-start').value, endLocal: get('oncall-range-end').value, slots: [] };
        if (!(wallTime(body.endLocal) > wallTime(body.startLocal))) throw new Error(text('invalid-range', 'End must be after start.'));
        const left = utcForLocal(body.startLocal, zone), right = utcForLocal(body.endLocal, zone);
        if (!Number.isFinite(left) || !Number.isFinite(right)) throw new Error(text('invalid-local', 'Invalid team local time.'));
        payload({ startUtc: new Date(left).toISOString(), endUtc: new Date(right).toISOString() });
        if (!generate) {
            try { body.slots = copyRange(state.slots, localTime(start, zone), localTime(end, zone), body.startLocal, body.endLocal, members); }
            catch (failure) { throw new Error(failure.message === 'departed' ? text('cannot-copy-departed', 'Cannot copy assignments for departed team members.') : text('invalid-range', 'End must be after start.')); }
        }
        const data = await request(`${root.dataset.previewUrl}?generate=${generate}`, body);
        if (revision !== sequence) return;
        if (data.errors?.length) throw new Error(data.errors.join('\n'));
        const destination = { start: Date.parse(data.startUtc), end: Date.parse(data.endUtc), slots: data.slots };
        const sameRange = destination.start === from && destination.end === until;
        if ((!sameRange && destination.start < until && destination.end > from) || state.ranges.some(item => item.start < destination.end && item.end > destination.start && !(item.start === destination.start && item.end === destination.end))) throw new Error(text('copy-overlap', 'The destination must not overlap the visible range or another copied range.'));
        if (conflicts(rangeDraft(destination)).size) throw new Error(overlapMessage());
        list('oncall-schedule-warnings', data.warnings);
        const replacing = data.replacedSlots.length || sameRange || state.ranges.some(item => item.start === destination.start && item.end === destination.end);
        if (replacing) confirmReplacement({ range: destination }, data.replacedSlots.length ? data.replacedSlots : sameRange ? normalized(state.slots) : state.ranges.find(item => item.start === destination.start).slots);
        else stage(destination);
    }
    async function assembledDraft(extraRange) {
        const body = payload(extraRange);
        const low = Date.parse(body.startUtc), high = Date.parse(body.endUtc);
        if (low !== from || high !== until || state.ranges.length) {
            const query = new URLSearchParams({ id: root.dataset.groupId, startUtc: body.startUtc, endUtc: body.endUtc });
            const saved = await request(`${root.dataset.getUrl}?${query}`);
            if (saved.version !== root.dataset.version) throw new Error(text('schedule-save-failed', 'Unable to save schedule. Refresh and try again.'));
            const ranges = [{ start: from, end: until }, ...state.ranges];
            const preserved = (saved.saved ? saved.slots : []).filter(slot => !ranges.some(range => Date.parse(slot.startsAtUtc) < range.end && Date.parse(slot.endsAtUtc) > range.start) && !body.slots.some(item => item.id && item.id === slot.id));
            body.slots.push(...submitted(preserved));
        }
        return body;
    }
    function clearOperation() {
        const left = utcForLocal(get('oncall-clear-start').value, zone), right = utcForLocal(get('oncall-clear-end').value, zone);
        if (!Number.isFinite(left) || !Number.isFinite(right)) throw new Error(text('invalid-local', 'Invalid team local time.'));
        if (right <= left) throw new Error(text('invalid-range', 'End must be after start.'));
        const autoExtendAction = get('oncall-clear-auto-action').value || null;
        if (root.dataset.autoExtend === 'true' && !['disable', 'regenerate'].includes(autoExtendAction)) throw new Error(text('clear-auto-required', 'Choose whether to disable automatic extension or regenerate cleared gaps.'));
        return { startUtc: new Date(left).toISOString(), endUtc: new Date(right).toISOString(), includeManual: get('oncall-clear-manual').checked, includeHistory: get('oncall-clear-history').checked, confirmHistory: false, autoExtendAction };
    }
    async function clearRange(clear) {
        cancelPending();
        const body = await assembledDraft(clear);
        body.clearRanges.push(clear);
        const data = await request(`${root.dataset.previewUrl}?clear=true`, body);
        if (data.errors?.length) throw new Error(data.errors.join('\n'));
        const slots = prepare(data.slots);
        const low = Date.parse(data.startUtc), high = Date.parse(data.endUtc);
        if (!(Number.isFinite(low) && Number.isFinite(high) && high > low) || !Array.isArray(data.clearRanges) ||
            data.clearRanges.some(range => !range || !Number.isFinite(Date.parse(range.clearedAtUtc)) ||
                !(Date.parse(range.startUtc) >= low && Date.parse(range.endUtc) <= high && Date.parse(range.endUtc) > Date.parse(range.startUtc)))) {
            throw new Error(text('schedule-save-failed', 'Unable to save schedule. Refresh and try again.'));
        }
        const candidate = { slots: [], ranges: [], clearRanges: data.clearRanges };
        if (low < from) candidate.ranges.push({ start: low, end: from, slots: [] });
        if (high > until) candidate.ranges.push({ start: until, end: high, slots: [] });
        // Keep crossing shifts whole and in one draft bucket so their saved IDs are never duplicated.
        for (const slot of slots) {
            const left = timeOf(slot, 'start', zone), right = timeOf(slot, 'end', zone);
            if (left < until && right > from) candidate.slots.push(slot);
            else candidate.ranges.find(range => left < range.end && right > range.start)?.slots.push(slot);
        }
        if (conflicts(candidate).size) throw new Error(overlapMessage());
        if (clear.autoExtendAction === 'regenerate') confirmReplacement({ state: candidate }, data.replacedSlots, true);
        else stageClear(candidate);
        list('oncall-schedule-warnings', data.warnings || []);
        draw(data.timelineWarnings);
    }
    function stageClear(candidate) {
        if (conflicts(candidate).size) { error(overlapMessage()); return; }
        undo.push(structuredClone(state)); redo.length = 0;
        state = candidate; selected = null; changed();
    }
    function beginClear() {
        if (busy) return;
        cancelPending();
        try {
            const clear = clearOperation();
            if (clear.includeHistory) {
                historyPending = clear;
                get('oncall-clear-history-range').textContent = `${localTime(clear.startUtc, zone)} → ${localTime(clear.endUtc, zone)} · ${zone}`;
                get('oncall-clear-history-confirm').hidden = false;
            } else return operation(() => clearRange(clear));
        } catch (failure) { error(failure.message); }
    }
    async function save() {
        if (conflicts().size) throw new Error(overlapMessage());
        const body = await assembledDraft();
        if (overlappingSlots(body.slots, zone).size) throw new Error(overlapMessage());
        if (body.clearRanges.some(range => range.autoExtendAction === 'regenerate')) body.confirmReplace = true;
        await request(root.dataset.saveUrl, body);
        dirty = false; window.location.reload();
    }
    get('oncall-dialog-apply').addEventListener('click', () => {
        const slot = structuredClone(editing.slot);
        slot.startLocal = get('oncall-dialog-start').value; slot.endLocal = get('oncall-dialog-end').value;
        if (!valid(slot)) { list('oncall-dialog-errors', [text('invalid-range', 'End must be after start.'), text('invalid-local', 'Invalid team local time.')]); return; }
        if (hasDeparted(slot) && (slot.startLocal !== state.slots[editing.index].startLocal || slot.endLocal !== state.slots[editing.index].endLocal)) { list('oncall-dialog-errors', [text('departed-edit', 'Remove departed members before changing shift times.')]); return; }
        const previous = state.slots[editing.index];
        if (slot.startLocal !== previous.startLocal || slot.endLocal !== previous.endLocal || !samePeople(slot.userIds, previous.userIds)) markManual(slot);
        if (commit(() => { state.slots[editing.index] = slot; }, true, false, 'oncall-dialog-errors')) closeEditor();
    });
    get('oncall-dialog-delete').addEventListener('click', () => { commit(() => state.slots.splice(editing.index, 1), true, true); selected = null; closeEditor(); chart.focus(); });
    get('oncall-dialog-cancel').addEventListener('click', closeEditor);
    dialog.addEventListener('cancel', event => { event.preventDefault(); closeEditor(); });
    get('oncall-add-shift').addEventListener('click', () => commit(() => state.slots.push({ startLocal: localTime(start, zone), endLocal: localTime(end, zone), userIds: [], source: 0 })));
    get('oncall-undo-schedule').addEventListener('click', () => travel(undo, redo));
    get('oncall-redo-schedule').addEventListener('click', () => travel(redo, undo));
    get('oncall-reset-schedule').addEventListener('click', () => commit(() => { state = structuredClone(original); selected = null; }, true, true));
    get('oncall-merge-overlaps').addEventListener('click', () => operation(async () => {
        const known = teamSlots();
        let low = Math.min(from, ...state.ranges.map(range => range.start), ...known.map(slot => timeOf(slot, 'start', zone)));
        let high = Math.max(until, ...state.ranges.map(range => range.end), ...known.map(slot => timeOf(slot, 'end', zone)));
        let body;
        // 扩大替换范围前读取完整排班，避免未显示的相邻班段被遗漏删除。
        while (true) {
            body = await assembledDraft({ startUtc: new Date(low).toISOString(), endUtc: new Date(high).toISOString() });
            const nextLow = Math.min(low, ...body.slots.map(slot => timeOf(slot, 'start', zone)));
            const nextHigh = Math.max(high, ...body.slots.map(slot => timeOf(slot, 'end', zone)));
            if (nextLow === low && nextHigh === high) break;
            low = nextLow; high = nextHigh;
        }
        const slots = prepare(normalized(mergeOverlaps(body.slots, zone)));
        const candidate = { slots: slots.filter(slot => timeOf(slot, 'start', zone) < until && timeOf(slot, 'end', zone) > from), ranges: [], clearRanges: state.clearRanges };
        if (low < from) candidate.ranges.push({ start: low, end: from, slots: slots.filter(slot => timeOf(slot, 'end', zone) <= from) });
        if (high > until) candidate.ranges.push({ start: until, end: high, slots: slots.filter(slot => timeOf(slot, 'start', zone) >= until) });
        stageClear(candidate);
    }));
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
        if (commit(() => { shiftTimes(slot, time - timeOf(slot, 'start', zone), zone); movePerson(slot, selected.person, target); })) select(selected.index, target);
    });
    get('oncall-save-schedule').addEventListener('click', () => operation(save));
    get('oncall-copy-range').addEventListener('click', () => operation(() => range(false)));
    get('oncall-generate')?.addEventListener('click', () => operation(() => range(true)));
    get('oncall-replace-cancel').addEventListener('click', cancelPending);
    get('oncall-replace-apply').addEventListener('click', () => {
        if (pending && !busy) {
            if (pending.state) stageClear(pending.state);
            else stage(pending.range);
        }
    });
    get('oncall-clear-range').addEventListener('click', beginClear);
    get('oncall-clear-history-cancel').addEventListener('click', cancelPending);
    get('oncall-clear-history-apply').addEventListener('click', () => {
        if (!historyPending || busy) return;
        const clear = { ...historyPending, confirmHistory: true };
        return operation(() => clearRange(clear));
    });
    for (const id of ['oncall-range-start', 'oncall-range-end', 'oncall-clear-start', 'oncall-clear-end', 'oncall-clear-manual', 'oncall-clear-history', 'oncall-clear-auto-action']) {
        get(id).addEventListener(id.endsWith('manual') || id.endsWith('history') || id.endsWith('action') ? 'change' : 'input', () => {
            sequence++; cancelPending();
            if (id === 'oncall-clear-end') clearEndEdited = true;
        });
    }
    get('oncall-range-start').value = localTime(end, zone);
    get('oncall-range-end').value = wallString(wallTime(localTime(end, zone)) + 7 * 86400000);
    get('oncall-clear-start').value = localTime(Date.now(), zone);
    get('oncall-clear-auto-options').hidden = root.dataset.autoExtend !== 'true';
    clearDefaults();
    window.addEventListener('beforeunload', event => { if (dirty) { event.preventDefault(); event.returnValue = ''; } });
    chart.addEventListener('oncall-packed', () => chart.querySelectorAll('.ac-gantt-lane:not(#oncall-warning-lane)').forEach(pack));
    renderRows(); draw();
    get('oncall-undo-schedule').disabled = true; get('oncall-redo-schedule').disabled = true;
    preview();
})();
