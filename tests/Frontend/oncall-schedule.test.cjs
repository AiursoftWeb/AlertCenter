const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const test = require('node:test');
const { localTime, copyRange, hoursFor, utcForLocal, timeOf, snapTime, movePerson, swapPeople, reminders, warningSegments } = require('../../src/Aiursoft.AlertCenter/wwwroot/scripts/oncall-schedule.js');
const source = fs.readFileSync(require.resolve('../../src/Aiursoft.AlertCenter/wwwroot/scripts/oncall-schedule.js'), 'utf8');
const ganttSource = fs.readFileSync(require.resolve('../../src/Aiursoft.AlertCenter/wwwroot/scripts/oncall-gantt.js'), 'utf8');
const hour = 3600000;
const warningText = {
    gap: 'Schedule contains a coverage gap.', overlap: 'Schedule contains overlapping shifts.',
    'unassigned-warning': 'Shift has no on-duty engineers.', 'rest-warning': 'An engineer has insufficient rest between shifts.',
    'continuous-warning': 'An engineer exceeds the continuous on-call threshold.', 'staff-warning': 'Shift staffing is below the team threshold.',
    'departed-warning': 'Future shift includes a departed team member.'
};
const start = '2026-10-11T16:00:00Z', end = '2026-10-18T16:00:00Z';
const members = [{ id: 'a', name: 'Alice <team>' }, { id: 'b', name: 'Bob' }];
const initial = [{ id: 'shift', startsAtUtc: start, endsAtUtc: '2026-10-15T14:00:00Z', userIds: ['a'] }];

test('team local times are independent of browser time zone and preserve DST transitions', () => {
    assert.equal(localTime(start, 'Asia/Shanghai'), '2026-10-12T00:00');
    assert.equal(localTime('2026-11-01T05:30:00Z', 'America/New_York'), '2026-11-01T01:30');
    assert.equal(localTime('2026-11-01T06:30:00Z', 'America/New_York'), '2026-11-01T01:30');
});
test('copy repeats arbitrary multi-person drafts, clips cross-week shifts and final partial cycle', () => {
    const slots = [{ startLocal: '2026-10-10T22:00', endLocal: '2026-10-16T22:00', userIds: ['a', 'b'] },
        { startLocal: '2026-10-16T22:00', endLocal: '2026-10-20T22:00', userIds: [] }];
    const copies = copyRange(slots, '2026-10-12T00:00', '2026-10-19T00:00', '2026-10-25T00:00', '2026-11-03T10:17', members);
    assert.equal(copies.length, 3);
    assert.deepEqual(copies[0], { startLocal: '2026-10-25T00:00', endLocal: '2026-10-29T22:00', userIds: ['a', 'b'], source: 0 });
    assert.equal(copies[1].endLocal, '2026-11-01T00:00');
    assert.equal(copies[2].endLocal, '2026-11-03T10:17');
    assert.equal(copies[0].id, undefined);
    assert.equal(slots[0].startLocal, '2026-10-10T22:00');
});
test('copy retains local weekly boundaries across daylight saving changes', () => {
    const copies = copyRange([{ startLocal: '2026-10-19T22:00', endLocal: '2026-10-26T00:00', userIds: ['a'] }],
        '2026-10-19T00:00', '2026-10-26T00:00', '2026-10-26T00:00', '2026-11-09T00:00', members);
    assert.equal(copies[1].startLocal, '2026-11-02T22:00');
    assert.equal(copies[1].endLocal, '2026-11-09T00:00');
});
test('copy rejects departed members and invalid ranges', () => {
    const slots = [{ startLocal: '2026-10-12T00:00', endLocal: '2026-10-19T00:00', userIds: ['gone'] }];
    assert.throws(() => copyRange(slots, '2026-10-12T00:00', '2026-10-19T00:00', '2026-10-19T00:00', '2026-10-26T00:00', members), /departed/);
    assert.throws(() => copyRange([], '2026-10-12T00:00', '2026-10-19T00:00', '2026-10-19T00:00', '2026-10-18T00:00', members), /range/);
});
test('duration statistics clip cross-week shifts and merge touching or overlapping duty per person', () => {
    const slots = [{ startsAtUtc: '2026-10-10T16:00:00Z', endsAtUtc: '2026-10-12T16:00:00Z', userIds: ['a'] },
        { startsAtUtc: start, endsAtUtc: '2026-10-13T16:00:00Z', userIds: ['a', 'b'] },
        { startsAtUtc: '2026-10-13T16:00:00Z', endsAtUtc: '2026-10-20T16:00:00Z', userIds: ['a'] }];
    assert.equal(hoursFor(slots, 'a', start, end), 168);
    assert.equal(hoursFor(slots, 'b', start, end), 48);
    assert.equal(hoursFor(slots, 'none', start, end), 0);
});

function setup(options = {}) {
    const zone = options.zone || 'Asia/Shanghai', rangeStart = options.start || start, rangeEnd = options.end || end;
    const people = options.members || members, slots = structuredClone(options.slots || initial);
    const contextSlots = structuredClone(options.context || []);
    const messages = { ...warningText, ...options.messages };
    const pendingHandlers = [], errors = [], elements = {}, timers = new Map(), captures = new Set();
    let document, hitTarget = null, now = 0, nextTimer = 1;
    const dataKey = name => name.slice(5).replace(/-([a-z])/g, (_, char) => char.toUpperCase());
    class BrowserEvent {
        constructor(type, init = {}) {
            Object.assign(this, { type, bubbles: false, cancelable: true, defaultPrevented: false, detail: 0, button: 0, pointerId: 1, pointerType: 'mouse', altKey: false, shiftKey: false, ctrlKey: false, metaKey: false }, init);
        }
        preventDefault() { if (this.cancelable) this.defaultPrevented = true; }
        stopPropagation() { this.stopped = true; }
    }
    class Element {
        constructor(tag = 'div') {
            this.tagName = tag.toUpperCase(); this.children = []; this.parentElement = null;
            this.dataset = {}; this.style = {}; this.attributes = {}; this.listeners = {};
            this.className = ''; this.value = ''; this.hidden = false; this.disabled = false; this.checked = false;
            this.scrollLeft = 0; this.clientWidth = 900; this.scrollWidth = 1600; this.open = false; this._text = '';
            this.classList = {
                contains: value => this.className.split(/\s+/).includes(value),
                add: (...values) => { this.className = [...new Set([...this.className.split(/\s+/).filter(Boolean), ...values])].join(' '); },
                remove: (...values) => { this.className = this.className.split(/\s+/).filter(value => !values.includes(value)).join(' '); },
                toggle: (value, force) => { const add = force ?? !this.classList.contains(value); this.classList[add ? 'add' : 'remove'](value); return add; }
            };
        }
        get textContent() { return this._text + this.children.map(child => child.textContent).join(''); }
        set textContent(value) { this.replaceChildren(); this._text = String(value); }
        append(...items) { for (const item of items) { item.remove(); item.parentElement = this; this.children.push(item); } }
        replaceChildren(...items) { for (const child of this.children) child.parentElement = null; this.children = []; this._text = ''; this.append(...items); }
        remove() { if (this.parentElement) this.parentElement.children = this.parentElement.children.filter(item => item !== this); this.parentElement = null; }
        setAttribute(key, value) {
            this.attributes[key] = String(value);
            if (key.startsWith('data-')) this.dataset[dataKey(key)] = String(value);
            if (key === 'class') this.className = String(value);
            if (key === 'id') this.id = String(value);
        }
        getAttribute(key) {
            if (key.startsWith('data-')) return this.dataset[dataKey(key)] ?? null;
            if (key === 'style') return Object.keys(this.style).length ? '' : null;
            if (key === 'class') return this.className;
            if (key === 'id') return this.id ?? null;
            return this.attributes[key] ?? null;
        }
        matches(selector) {
            return selector.split(',').some(alternative => {
                let rest = alternative.trim();
                const negative = [...rest.matchAll(/:not\(([^)]+)\)/g)].map(match => match[1]);
                if (negative.some(value => this.matches(value))) return false;
                rest = rest.replace(/:not\([^)]+\)/g, '');
                for (const match of rest.matchAll(/\[([^\]=]+)(?:="([^"]*)")?\]/g)) {
                    const value = this.getAttribute(match[1]);
                    if (value === null || (match[2] !== undefined && value !== match[2])) return false;
                }
                rest = rest.replace(/\[[^\]]+\]/g, '');
                for (const match of rest.matchAll(/\.([\w-]+)/g)) if (!this.classList.contains(match[1])) return false;
                for (const match of rest.matchAll(/#([\w-]+)/g)) if (this.id !== match[1]) return false;
                rest = rest.replace(/[.#][\w-]+/g, '').trim();
                return !rest || rest === '*' || rest.toUpperCase() === this.tagName;
            });
        }
        closest(selector) { for (let node = this; node; node = node.parentElement) if (node.matches(selector)) return node; return null; }
        querySelectorAll(selector) { return this.children.flatMap(child => [...(child.matches(selector) ? [child] : []), ...child.querySelectorAll(selector)]); }
        querySelector(selector) { return this.querySelectorAll(selector)[0] || null; }
        addEventListener(type, handler) { (this.listeners[type] ??= []).push(handler); }
        dispatchEvent(event) {
            event.target ??= this;
            for (let node = this; node; node = event.bubbles && !event.stopped ? node.parentElement : null) {
                event.currentTarget = node;
                for (const handler of node.listeners[event.type] || []) {
                    try { const result = handler(event); if (result?.then) pendingHandlers.push(result.catch(error => errors.push(error))); }
                    catch (error) { errors.push(error); }
                }
            }
            return !event.defaultPrevented;
        }
        click() { if (!this.disabled) this.dispatchEvent(new BrowserEvent('click', { bubbles: true })); }
        focus() { document.activeElement = this; this.dispatchEvent(new BrowserEvent('focusin', { bubbles: true })); }
        showModal() { this.open = true; }
        close() { this.open = false; }
        setPointerCapture(id) { captures.add(id); }
        hasPointerCapture(id) { return captures.has(id); }
        releasePointerCapture(id) { captures.delete(id); }
        getBoundingClientRect() {
            const width = this.classList.contains('ac-gantt-lane') ? (Number(chart.dataset.duration) + Number(chart.dataset.beforeHours || 0) + Number(chart.dataset.afterHours || 0)) * 6 : 900;
            return { left: 100, right: 100 + width, top: 0, bottom: 600, width, height: 600 };
        }
    }
    document = new Element('document'); document.activeElement = null;
    document.createElement = tag => new Element(tag);
    document.createTextNode = value => { const text = new Element('#text'); text.textContent = value; return text; };
    document.getElementById = id => elements[id] || null;
    document.elementFromPoint = () => hitTarget;
    function create(id, tag = 'div', parent = document) { const element = new Element(tag); element.id = `oncall-${id}`; elements[element.id] = element; parent.append(element); return element; }
    const root = create('schedule'), chart = create('gantt');
    const grid = new Element(); grid.className = 'ac-gantt'; chart.append(grid);
    chart.dataset.duration = String((Date.parse(rangeEnd) - Date.parse(rangeStart)) / hour);
    Object.assign(root.dataset, { groupId: 'group', version: 'version', zone, start: rangeStart, end: rangeEnd, slots: JSON.stringify(slots), members: JSON.stringify(people), context: JSON.stringify(contextSlots), previewUrl: '/preview', saveUrl: '/save', getUrl: '/saved', generateUrl: '/generate', autoExtend: String(options.autoExtend || false), lastShiftEnd: options.lastShiftEnd || '', ...options.thresholds });
    const csrf = new Element('input'); csrf.setAttribute('name', '__RequestVerificationToken'); csrf.value = 'csrf'; root.append(csrf);
    for (const [key, value] of Object.entries(messages)) { const label = new Element('span'); label.dataset.scheduleText = key; label.textContent = value; root.append(label); }
    for (const id of ['shift-rows', 'draft-notice', 'replace-confirm', 'replace-regenerate-hint', 'replaced-shifts', 'schedule-errors', 'schedule-warnings', 'copy-drafts', 'drag-tooltip', 'total-shifts', 'total-staff', 'total-hours', 'published-status', 'clear-history-confirm', 'clear-history-range', 'clear-auto-options']) create(id, id === 'shift-rows' ? 'tbody' : 'div', root);
    for (const id of ['save-schedule', 'reset-schedule', 'undo-schedule', 'redo-schedule', 'add-shift', 'copy-range', 'generate', 'replace-apply', 'replace-cancel', 'swap-week', 'move-selected', 'clear-range', 'clear-history-apply', 'clear-history-cancel']) create(id, 'button', root);
    for (const id of ['range-start', 'range-end', 'move-start', 'clear-start', 'clear-end', 'clear-manual', 'clear-history']) create(id, 'input', root);
    for (const id of ['swap-first', 'swap-second', 'move-member', 'clear-auto-action']) create(id, 'select', root);
    const dialog = create('shift-dialog', 'dialog', root);
    for (const id of ['start', 'end', 'members', 'errors', 'apply', 'delete', 'cancel']) create(`dialog-${id}`, ['start', 'end'].includes(id) ? 'input' : ['apply', 'delete', 'cancel'].includes(id) ? 'button' : 'div', dialog);
    for (const id of ['shift-detail', 'shift-summary', 'shift-start', 'shift-end']) create(id);
    for (const id of ['warning-lane', 'handover-lane']) create(id, 'div', grid).className = 'ac-gantt-lane';
    const rows = new Map();
    for (const member of [{ id: '', name: 'Unassigned' }, ...people]) {
        const row = new Element(); row.dataset = { userId: member.id, name: member.name, color: '0' };
        if (!member.id) { row.id = 'oncall-unassigned-row'; elements[row.id] = row; }
        const hours = new Element(), lane = new Element(); hours.className = 'ac-gantt-hours'; lane.className = 'ac-gantt-lane';
        row.append(hours, lane);
        if (member.id && !member.departed) { const week = new Element('button'); week.dataset.weekUser = member.id; week.className = 'ac-gantt-week'; row.append(week); }
        grid.append(row); rows.set(member.id, row);
    }
    const readonly = contextSlots.map(slot => {
        const bar = new Element('button'); bar.className = `ac-gantt-shift ${slot.external ? 'ac-gantt-external' : 'ac-gantt-adjacent'}`;
        Object.assign(bar.dataset, { readonly: 'true', start: localTime(slot.startsAtUtc, zone), end: localTime(slot.endsAtUtc, zone), summary: slot.external ? 'Other team' : 'Adjacent saved shift' });
        bar.style.left = `${(Date.parse(slot.startsAtUtc) - Date.parse(rangeStart)) / (Date.parse(rangeEnd) - Date.parse(rangeStart)) * 100}%`;
        bar.style.width = `${(Date.parse(slot.endsAtUtc) - Date.parse(slot.startsAtUtc)) / (Date.parse(rangeEnd) - Date.parse(rangeStart)) * 100}%`;
        if (Date.parse(slot.endsAtUtc) <= Date.parse(rangeStart)) bar.dataset.edge = 'previous';
        if (Date.parse(slot.startsAtUtc) >= Date.parse(rangeEnd)) bar.dataset.edge = 'next';
        (rows.get(slot.userIds[0]) || rows.get('')).querySelector('.ac-gantt-lane').append(bar); return bar;
    });
    const toggles = {};
    for (const edge of ['previous', 'next']) { const button = new Element('button'); button.dataset.oncallEdge = edge; button.setAttribute('aria-expanded', 'false'); document.append(button); toggles[edge] = button; }
    const savedWarning = new Element(); savedWarning.dataset.oncallSavedWarning = ''; document.append(savedWarning);
    for (const id of ['draft-notice', 'replace-confirm', 'drag-tooltip', 'clear-history-confirm']) elements[`oncall-${id}`].hidden = true;
    const requests = [], deferred = [];
    const window = new Element('window'); window.reloads = 0; window.location = { reload() { window.reloads++; } };
    const resolved = slot => {
        const copy = structuredClone(slot);
        for (const edge of ['start', 'end']) {
            const key = edge === 'start' ? 'startsAtUtc' : 'endsAtUtc', local = copy[`${edge}Local`];
            if (local && (!copy[key] || localTime(copy[key], zone) !== local)) copy[key] = new Date(utcForLocal(local, zone)).toISOString();
            delete copy[`${edge}Local`];
        }
        return copy;
    };
    const responseFor = (url, body) => {
        if (url.startsWith('/saved?')) {
            const query = new URL(url, 'https://example.invalid').searchParams;
            const left = Date.parse(query.get('startUtc')), right = Date.parse(query.get('endUtc'));
            return { version: options.savedVersion || 'version', saved: options.saved !== false, autoExtend: options.autoExtend || false, lastShiftEndUtc: options.lastShiftEnd || null, slots: structuredClone(options.savedSlots || slots).filter(slot => Date.parse(slot.startsAtUtc) < right && Date.parse(slot.endsAtUtc) > left) };
        }
        const startUtc = body.startUtc || new Date(utcForLocal(body.startLocal, zone)).toISOString();
        const endUtc = body.endUtc || new Date(utcForLocal(body.endLocal, zone)).toISOString();
        const resultSlots = url.includes('generate=true') ? (options.generatedSlots || [{ startsAtUtc: startUtc, endsAtUtc: endUtc, userIds: ['b'], source: 1 }]) : body.slots.map(resolved);
        if (url === '/preview?clear=true') return {
            version: 'version', startUtc, endUtc, slots: typeof options.clearSlots === 'function' ? options.clearSlots(body.slots.map(resolved), body) : options.clearSlots || [], replacedSlots: options.clearReplacedSlots || body.slots.map(resolved), warnings: [], timelineWarnings: [],
            clearRanges: body.clearRanges.map(range => ({ ...range, clearedAtUtc: range.clearedAtUtc || options.serverNow || '2026-10-12T04:13:45.123Z' })), errors: ['Clear rejected'], ...options.clearResponse
        };
        return { version: url === '/save' ? 'new-version' : 'version', startUtc, endUtc, slots: resultSlots, replacedSlots: url.includes('?generate=') ? structuredClone(options.replacedSlots || []) : [], warnings: [], timelineWarnings: options.serverWarnings || [], errors: ['Version conflict'] };
    };
    class ClockDate extends Date { static now() { return options.now ? Date.parse(options.now) : Date.now(); } }
    vm.runInNewContext(ganttSource + '\n' + source, {
        document, window, Intl, Date: ClockDate, structuredClone, URLSearchParams, Event: BrowserEvent,
        getLocalizedText: (key, fallback) => messages[key] ?? fallback,
        setTimeout(fn, delay) { const id = nextTimer++; timers.set(id, { fn, at: now + delay }); return id; },
        clearTimeout(id) { timers.delete(id); },
        fetch: async (url, requestOptions = {}) => {
            const body = requestOptions.body ? JSON.parse(requestOptions.body) : undefined;
            const record = { url, body, options: requestOptions }; requests.push(record);
            if (options.deferPreview && url === '/preview') return new Promise(resolve => deferred.push({ record, resolve: data => resolve({ ok: true, json: async () => data }) }));
            return { ok: !((options.fail && url === '/save') || (options.failClear && url === '/preview?clear=true')), json: async () => responseFor(url, body) };
        }
    });
    const flush = async () => {
        await new Promise(resolve => setImmediate(resolve));
        while (pendingHandlers.length) await Promise.all(pendingHandlers.splice(0));
        if (errors.length) throw errors.shift();
    };
    const get = id => elements[`oncall-${id}`];
    const fire = async (element, type, init = {}) => { const event = new BrowserEvent(type, { bubbles: true, ...init }); element.dispatchEvent(event); await flush(); return event; };
    const advance = async amount => {
        const until = now + amount;
        while (true) {
            const due = [...timers].filter(([, timer]) => timer.at <= until).sort((a, b) => a[1].at - b[1].at)[0];
            if (!due) break;
            now = due[1].at; timers.delete(due[0]); const result = due[1].fn();
            if (result?.then) result.catch(error => errors.push(error));
            await flush();
        }
        now = until; await flush();
    };
    const lane = id => rows.get(id).querySelector('.ac-gantt-lane');
    const bar = (index = 0, person = 'a') => lane(person).querySelector(`[data-slot-index="${index}"]`);
    const at = hours => 100 + (hours + Number(chart.dataset.beforeHours || 0)) * 6;
    const pointer = async (type, target, hours, init = {}) => {
        hitTarget = target;
        return fire(type === 'pointerdown' ? target : chart, type, { clientX: at(hours), clientY: 100, ...init });
    };
    const drag = async (element, fromHour, toHour, target = lane('a'), init = {}) => {
        const targetIndex = target.dataset.slotIndex, targetPerson = target.closest('[data-user-id]')?.dataset.userId;
        await pointer('pointerdown', element, fromHour, init);
        await pointer('pointermove', target, toHour, { ...init, clientY: 140 });
        await pointer('pointerup', targetIndex == null ? target : bar(Number(targetIndex), targetPerson) || lane(targetPerson), toHour, { ...init, clientY: 140 });
    };
    const draft = () => get('shift-rows').children.map(row => ({ startLocal: row.children[0].children[0].value, endLocal: row.children[1].children[0].value, userIds: row.children[2].querySelectorAll('input').filter(input => input.checked).map(input => input.value) }));
    const click = async id => { (typeof id === 'string' ? get(id) : id).click(); await flush(); };
    const input = async (element, value) => { element.value = value; await fire(element, 'input'); };
    const check = async (container, id, checked) => { const checkbox = container.querySelectorAll('input').find(item => item.value === id); checkbox.checked = checked; await fire(checkbox, 'change'); };
    const request = url => requests.filter(item => item.url === url);
    return { get, root, chart, document, window, rows, readonly, toggles, savedWarning, requests, deferred, flush, fire, advance, pointer, drag, lane, bar, at, draft, click, input, check, request, captures, preview: () => advance(250) };
}

function shift(left, right, userIds = ['a'], id) {
    return { ...(id ? { id } : {}), startsAtUtc: new Date(Date.parse(start) + left * hour).toISOString(), endsAtUtc: new Date(Date.parse(start) + right * hour).toISOString(), userIds };
}
function warnings(ui) { return ui.get('schedule-warnings').children.map(item => item.textContent); }
function tableInput(ui, row, edge) { return ui.get('shift-rows').children[row].children[edge === 'start' ? 0 : 1].children[0]; }

test('UTC conversion rejects skipped time, resolves repeated time to standard time, and preserves existing UTC occurrence', () => {
    assert(Number.isNaN(utcForLocal('2026-03-08T02:30', 'America/New_York')));
    assert.equal(new Date(utcForLocal('2026-11-01T01:30', 'America/New_York')).toISOString(), '2026-11-01T06:30:00.000Z');
    assert.equal(timeOf({ startsAtUtc: '2026-11-01T05:30:00Z', startLocal: '2026-11-01T01:30' }, 'start', 'America/New_York'), Date.parse('2026-11-01T05:30:00Z'));
    assert.equal(new Date(snapTime(Date.parse('2026-10-12T01:17:45Z'), 'Asia/Shanghai')).toISOString(), '2026-10-12T01:30:00.000Z');
    assert.equal(new Date(snapTime(Date.parse('2026-10-12T01:17:45Z'), 'Asia/Shanghai', true)).toISOString(), '2026-10-12T01:17:00.000Z');
});
test('assignment helpers preserve companions, deduplicate and fully unassign; swapping preserves multi-person membership', () => {
    const slot = { userIds: ['a', 'b'] }; movePerson(slot, 'a', 'b'); assert.deepEqual(slot.userIds, ['b']);
    slot.userIds = ['a', 'b']; movePerson(slot, 'a', ''); assert.deepEqual(slot.userIds, []);
    const slots = [{ userIds: ['a', 'b'] }, { userIds: ['a'] }, { userIds: [] }];
    swapPeople(slots, 'a', 'b'); assert.deepEqual(slots.map(item => item.userIds), [['b', 'a'], ['b'], []]);
});

test('body pointer drag moves time and engineer, preserves duration and supports one-step undo', async () => {
    const ui = setup({ slots: [shift(12, 20)] }); await ui.flush();
    await ui.drag(ui.bar(), 14, 16.2, ui.lane('b'));
    assert.deepEqual(ui.draft(), [{ startLocal: '2026-10-12T14:00', endLocal: '2026-10-12T22:00', userIds: ['b'] }]);
    assert.equal(ui.bar(0, 'a'), null); assert(ui.bar(0, 'b'));
    assert.equal(ui.get('drag-tooltip').hidden, true); assert.equal(ui.captures.size, 0);
    assert.equal(ui.get('undo-schedule').disabled, false); assert.equal(ui.request('/save').length, 0);
    await ui.click('undo-schedule'); assert.equal(ui.draft()[0].startLocal, '2026-10-12T12:00');
    assert.deepEqual(ui.draft()[0].userIds, ['a']);
});
test('vertical drag preserves off-grid minutes and dropping onto unassigned removes all assignees', async () => {
    const ui = setup({ slots: [shift(12 + 17 / 60, 20 + 17 / 60, ['a', 'b'])] }); await ui.flush();
    await ui.drag(ui.bar(), 14, 14, ui.lane('b'));
    assert.equal(ui.draft()[0].startLocal, '2026-10-12T12:17');
    assert.deepEqual(ui.draft()[0].userIds, ['b']);
    await ui.drag(ui.bar(0, 'b'), 14, 14, ui.lane(''));
    assert.deepEqual(ui.draft()[0].userIds, []); assert(ui.bar(0, ''));
});
test('dropping onto another occupied shift swaps assignments without changing its boundaries', async () => {
    const ui = setup({ slots: [shift(12, 20, ['a']), shift(12, 20, ['b'])] }); await ui.flush();
    await ui.drag(ui.bar(0, 'a'), 14, 14, ui.bar(1, 'b'));
    assert.deepEqual(ui.draft().map(slot => slot.userIds), [['b'], ['a']]);
    assert(ui.draft().every(slot => slot.startLocal === '2026-10-12T12:00' && slot.endLocal === '2026-10-12T20:00'));
});
test('start and end handles snap at thirty minutes, Alt preserves minutes, and cannot invert a shift', async () => {
    const ui = setup({ slots: [shift(12, 20)] }); await ui.flush();
    await ui.drag(ui.bar().querySelector('[data-resize="start"]'), 12, 13.3);
    assert.equal(ui.draft()[0].startLocal, '2026-10-12T13:30');
    await ui.drag(ui.bar().querySelector('[data-resize="end"]'), 20, 21.21);
    assert.equal(ui.draft()[0].endLocal, '2026-10-12T21:00');
    await ui.drag(ui.bar().querySelector('[data-resize="end"]'), 21, 22 + 17 / 60, ui.lane('a'), { altKey: true });
    assert.equal(ui.draft()[0].endLocal, '2026-10-12T22:17');
    await ui.drag(ui.bar().querySelector('[data-resize="start"]'), 13.5, 14 + 13 / 60, ui.lane('a'), { altKey: true });
    assert.equal(ui.draft()[0].startLocal, '2026-10-12T14:13');
    await ui.drag(ui.bar().querySelector('[data-resize="start"]'), 14 + 13 / 60, 23);
    assert.equal(ui.draft()[0].startLocal, '2026-10-12T22:16');
});
test('shared handover pointer and keyboard edits couple both boundaries without gap or overlap', async () => {
    const ui = setup({ slots: [shift(0, 12), shift(12, 24, ['b'])] }); await ui.flush();
    await ui.drag(ui.get('handover-lane').children[0], 12, 13.3, ui.get('handover-lane'));
    assert.equal(ui.draft()[0].endLocal, '2026-10-12T13:30'); assert.equal(ui.draft()[1].startLocal, '2026-10-12T13:30');
    await ui.fire(ui.get('handover-lane').children[0], 'keydown', { key: 'ArrowLeft', altKey: true });
    assert.equal(ui.draft()[0].endLocal, '2026-10-12T13:29'); assert.equal(ui.draft()[1].startLocal, '2026-10-12T13:29');
    await ui.click('undo-schedule'); assert.equal(ui.draft()[1].startLocal, '2026-10-12T13:30');
});
test('blank drag creates multi-hour and exact minute-duration shifts, including reverse drags and unassigned rows', async () => {
    const ui = setup({ slots: [] }); await ui.flush();
    await ui.drag(ui.lane('b'), 8.2, 13.3, ui.lane('b'));
    assert.deepEqual(ui.draft()[0], { startLocal: '2026-10-12T08:00', endLocal: '2026-10-12T13:30', userIds: ['b'] });
    await ui.drag(ui.lane(''), 25 + 17 / 60, 24 + 13 / 60, ui.lane(''), { altKey: true });
    assert.deepEqual(ui.draft()[1], { startLocal: '2026-10-13T00:13', endLocal: '2026-10-13T01:17', userIds: [] });
    await ui.pointer('pointerdown', ui.lane('a'), 30, { altKey: true });
    await ui.pointer('pointermove', ui.lane('a'), 30 + 1 / 60, { altKey: true, clientY: 120 });
    await ui.pointer('pointerup', ui.lane('a'), 30 + 1 / 60, { altKey: true, clientY: 120 });
    assert.equal(ui.draft()[2].endLocal, '2026-10-13T06:01');
    assert.equal(ui.request('/save').length, 0);
});

test('click popup edits exact times and zero or multiple members in the shared table/chart draft', async () => {
    const ui = setup({ slots: [shift(12, 20)] }); await ui.flush();
    await ui.click(ui.bar()); assert(ui.get('shift-dialog').open); assert.equal(ui.document.activeElement, ui.get('dialog-start'));
    ui.get('dialog-start').value = '2026-10-12T12:13'; ui.get('dialog-end').value = '2026-10-12T20:17';
    await ui.check(ui.get('dialog-members'), 'b', true); await ui.click('dialog-apply');
    assert(!ui.get('shift-dialog').open);
    assert.deepEqual(ui.draft()[0], { startLocal: '2026-10-12T12:13', endLocal: '2026-10-12T20:17', userIds: ['a', 'b'] });
    assert(ui.bar(0, 'a')); assert(ui.bar(0, 'b'));
    await ui.click(ui.bar()); await ui.check(ui.get('dialog-members'), 'a', false); await ui.check(ui.get('dialog-members'), 'b', false); await ui.click('dialog-apply');
    assert.deepEqual(ui.draft()[0].userIds, []); assert(ui.bar(0, '')); assert(warnings(ui).includes(warningText['unassigned-warning']));
});
test('popup cancel and native Escape discard edits; invalid times retain dialog and delete is undoable', async () => {
    const ui = setup({ slots: [shift(12, 20)] }); await ui.flush();
    const original = ui.draft();
    await ui.click(ui.bar()); ui.get('dialog-start').value = '2026-10-14T00:00'; await ui.click('dialog-apply');
    assert(ui.get('shift-dialog').open); assert(ui.get('dialog-errors').children.length > 0); assert.deepEqual(ui.draft(), original);
    await ui.click('dialog-cancel'); assert.deepEqual(ui.draft(), original);
    await ui.click(ui.bar()); await ui.check(ui.get('dialog-members'), 'b', true);
    const cancel = await ui.fire(ui.get('shift-dialog'), 'cancel', { bubbles: false });
    assert(cancel.defaultPrevented); assert(!ui.get('shift-dialog').open); assert.deepEqual(ui.draft(), original);
    await ui.click(ui.bar()); await ui.click('dialog-delete'); assert.equal(ui.draft().length, 0);
    await ui.click('undo-schedule'); assert.deepEqual(ui.draft(), original);
});
test('table input updates chart immediately and keeps untouched UTC fields in one save request', async () => {
    const ui = setup(); await ui.flush();
    await ui.input(tableInput(ui, 0, 'end'), '2026-10-17T22:17');
    await ui.check(ui.get('shift-rows').children[0].children[2], 'b', true);
    assert.equal(ui.bar().dataset.end, '2026-10-17T22:17'); assert(ui.bar(0, 'b'));
    await ui.preview(); assert.equal(ui.bar().dataset.end, '2026-10-17T22:17');
    await ui.click('save-schedule');
    const save = ui.request('/save'); assert.equal(save.length, 1);
    assert.equal(save[0].body.slots[0].endLocal, '2026-10-17T22:17');
    assert.equal(save[0].body.slots[0].startLocal, undefined); assert.equal(save[0].body.slots[0].startsAtUtc, start);
    assert.deepEqual(save[0].body.slots[0].userIds, ['a', 'b']);
    assert.equal(save[0].options.headers.RequestVerificationToken, 'csrf'); assert.equal(save[0].options.credentials, 'same-origin');
    assert.equal(ui.window.reloads, 1);
});
test('whole-week swap covers every draft shift, preserves companions and supports undo, redo and reset undo', async () => {
    const ui = setup({ slots: [shift(0, 8), shift(8, 16, ['b']), shift(16, 24, ['a', 'b']), shift(24, 30, [])] }); await ui.flush();
    const original = ui.draft(); ui.get('swap-first').value = 'a'; ui.get('swap-second').value = 'b';
    await ui.click('swap-week'); const swapped = ui.draft(); assert.deepEqual(swapped.map(slot => slot.userIds), [['b'], ['a'], ['a', 'b'], []]);
    await ui.click('undo-schedule'); assert.deepEqual(ui.draft(), original);
    await ui.click('redo-schedule'); assert.deepEqual(ui.draft(), swapped);
    await ui.click('reset-schedule'); assert.deepEqual(ui.draft(), original); assert.equal(ui.get('draft-notice').hidden, true);
    await ui.click('undo-schedule'); assert.deepEqual(ui.draft(), swapped); assert.equal(ui.get('draft-notice').hidden, false);
    ui.get('swap-second').value = 'a'; await ui.click('swap-week'); assert.deepEqual(ui.draft(), swapped); assert(ui.get('schedule-errors').children.length);
});

test('keyboard arrows move time and rows, Shift resizes, Enter opens, Delete removes, and Ctrl/Meta Z travel history', async () => {
    const ui = setup({ slots: [shift(12, 20)] }); await ui.flush();
    await ui.fire(ui.bar(), 'keydown', { key: 'ArrowRight' }); assert.equal(ui.draft()[0].startLocal, '2026-10-12T12:30');
    await ui.fire(ui.bar(), 'keydown', { key: 'ArrowLeft', altKey: true }); assert.equal(ui.draft()[0].startLocal, '2026-10-12T12:29');
    await ui.fire(ui.bar(), 'keydown', { key: 'ArrowDown' }); assert.deepEqual(ui.draft()[0].userIds, ['b']);
    await ui.fire(ui.bar(0, 'b'), 'keydown', { key: 'ArrowUp', shiftKey: true }); assert.equal(ui.draft()[0].startLocal, '2026-10-12T11:59');
    await ui.fire(ui.bar(0, 'b'), 'keydown', { key: 'ArrowRight', shiftKey: true, altKey: true }); assert.equal(ui.draft()[0].endLocal, '2026-10-12T20:30');
    await ui.fire(ui.bar(0, 'b'), 'keydown', { key: 'Enter' }); assert(ui.get('shift-dialog').open); await ui.click('dialog-cancel');
    await ui.fire(ui.bar(0, 'b'), 'keydown', { key: 'Delete' }); assert.equal(ui.draft().length, 0);
    await ui.fire(ui.chart, 'keydown', { key: 'z', ctrlKey: true }); assert.equal(ui.draft().length, 1);
    await ui.fire(ui.chart, 'keydown', { key: 'Z', metaKey: true, shiftKey: true }); assert.equal(ui.draft().length, 0);
    await ui.fire(ui.chart, 'keydown', { key: 'z', ctrlKey: true });
    const before = ui.draft(); await ui.fire(tableInput(ui, 0, 'start'), 'keydown', { key: 'z', ctrlKey: true }); assert.deepEqual(ui.draft(), before);
});
test('Escape cancels a moving pointer draft without adding undo history', async () => {
    const ui = setup({ slots: [shift(12, 20)] }); await ui.flush();
    const original = ui.draft();
    await ui.pointer('pointerdown', ui.bar(), 14);
    await ui.pointer('pointermove', ui.lane('b'), 18, { clientY: 140 }); assert.equal(ui.get('drag-tooltip').hidden, false);
    await ui.fire(ui.chart, 'keydown', { key: 'Escape' });
    await ui.pointer('pointerup', ui.lane('b'), 18);
    assert.deepEqual(ui.draft(), original); assert.equal(ui.get('drag-tooltip').hidden, true); assert.equal(ui.get('undo-schedule').disabled, true);
    assert(ui.bar()); assert.equal(ui.bar(0, 'b'), null);
});
test('touch drag requires 400ms long press, cancels early movement and pointercancel, then allows a held drag', async () => {
    const ui = setup({ slots: [shift(12, 20)] }); await ui.flush();
    const original = ui.draft(); const touch = { pointerType: 'touch' };
    await ui.pointer('pointerdown', ui.bar(), 14, touch); await ui.advance(399);
    await ui.pointer('pointermove', ui.lane('b'), 16, { ...touch, clientY: 140 }); await ui.advance(1);
    await ui.pointer('pointerup', ui.lane('b'), 16, touch); assert.deepEqual(ui.draft(), original);
    await ui.pointer('pointerdown', ui.bar(), 14, touch); await ui.advance(400);
    await ui.pointer('pointermove', ui.lane('b'), 16, { ...touch, clientY: 140 });
    await ui.pointer('pointercancel', ui.lane('b'), 16, touch); assert.deepEqual(ui.draft(), original); assert.equal(ui.bar(0, 'b'), null);
    await ui.pointer('pointerdown', ui.bar(), 14, touch); await ui.advance(400);
    await ui.pointer('pointermove', ui.lane('b'), 16, { ...touch, clientY: 140 });
    await ui.pointer('pointerup', ui.lane('b'), 16, touch);
    assert.equal(ui.draft()[0].startLocal, '2026-10-12T14:00'); assert.deepEqual(ui.draft()[0].userIds, ['b']);
});
test('expanded horizontal context preserves time coordinate mapping and cancels an active gesture on collapse', async () => {
    const ui = setup({ slots: [shift(12, 20)] }); await ui.flush();
    await ui.click(ui.toggles.previous); await ui.click(ui.toggles.next);
    assert.equal(ui.chart.dataset.beforeHours, '36'); assert.equal(ui.chart.dataset.afterHours, '36');
    assert.equal(ui.toggles.previous.getAttribute('aria-expanded'), 'true');
    assert(Math.abs(parseFloat(ui.bar().style.left) - 20) < 0.001);
    await ui.drag(ui.bar(), 14, 16, ui.lane('b'));
    assert.equal(ui.draft()[0].startLocal, '2026-10-12T14:00'); assert.equal(ui.draft()[0].endLocal, '2026-10-12T22:00');
    await ui.pointer('pointerdown', ui.bar(0, 'b'), 16); await ui.pointer('pointermove', ui.lane('a'), 18, { clientY: 140 });
    await ui.click(ui.toggles.previous); await ui.pointer('pointerup', ui.lane('a'), 18);
    assert.equal(ui.draft()[0].startLocal, '2026-10-12T14:00'); assert.deepEqual(ui.draft()[0].userIds, ['b']);
});

test('adjacent and cross-team bars cannot be edited or targeted, survive redraw, and never enter stats or requests', async () => {
    const external = { ...shift(8, 30, ['a'], 'external'), external: true }, adjacent = shift(-12, 0, ['a'], 'adjacent');
    const ui = setup({ slots: [shift(12, 20)], context: [external, adjacent] }); await ui.flush();
    const original = ui.draft();
    for (const target of ui.readonly) {
        await ui.drag(target, 14, 16, ui.lane('b')); await ui.click(target); await ui.fire(target, 'keydown', { key: 'Delete' });
        assert.deepEqual(ui.draft(), original); assert(!ui.get('shift-dialog').open);
        await ui.drag(ui.bar(), 14, 16, target); assert.deepEqual(ui.draft(), original);
    }
    await ui.drag(ui.bar(), 14, 16, ui.lane('b')); await ui.preview();
    assert(ui.readonly.every(bar => bar.parentElement));
    assert.equal(ui.get('total-shifts').textContent, '1'); assert.equal(ui.get('total-staff').textContent, '1'); assert.equal(ui.get('total-hours').textContent, '8.00 hours');
    assert.equal(ui.rows.get('a').querySelector('.ac-gantt-hours').textContent, '0 · 0.00 hours');
    assert.equal(ui.rows.get('b').querySelector('.ac-gantt-hours').textContent, '1 · 8.00 hours');
    await ui.click('save-schedule');
    for (const request of ui.requests.filter(item => item.body)) assert(request.body.slots.every(slot => slot.id !== 'external' && slot.id !== 'adjacent'));
});
test('departed members cannot receive drops, resize or copy, and removing them restores normal editing', async () => {
    const ui = setup({ slots: [shift(12, 20, ['gone'])], members: [...members, { id: 'gone', name: 'Former engineer', departed: true }] }); await ui.flush();
    assert.equal(ui.bar(0, 'gone').querySelector('[data-resize]'), null);
    assert.equal(tableInput(ui, 0, 'start').disabled, true); assert(warnings(ui).includes(warningText['departed-warning']));
    await ui.fire(ui.bar(0, 'gone'), 'keydown', { key: 'ArrowRight' }); assert.equal(ui.draft()[0].startLocal, '2026-10-12T12:00');
    await ui.click('copy-range'); assert.equal(ui.request('/preview?generate=false').length, 0); assert.match(ui.get('schedule-errors').textContent, /departed/);
    await ui.click(ui.bar(0, 'gone')); await ui.check(ui.get('dialog-members'), 'gone', false); await ui.check(ui.get('dialog-members'), 'a', true); await ui.click('dialog-apply');
    assert.equal(tableInput(ui, 0, 'start').disabled, false); assert(!warnings(ui).includes(warningText['departed-warning']));
    await ui.drag(ui.bar(), 14, 16, ui.lane('gone')); assert.deepEqual(ui.draft()[0].userIds, ['a']);
});
test('warning sweep merges identical adjacent sets, deduplicates messages, clips bounds and leaves gaps empty', () => {
    const warning = (left, right, message) => ({ startsAtUtc: left, endsAtUtc: right, message });
    assert.deepEqual(warningSegments([
        warning(-5, 4, 'A'), warning(2, 8, 'A'), warning(8, 10, 'A'),
        warning(3, 6, 'B'), warning(3, 6, 'B'), warning(12, 25, 'C'),
        warning(4, 4, 'empty'), warning(8, 2, 'inverted'), warning('invalid', 9, 'invalid'), warning(30, 40, 'outside')
    ], 0, 20), [
        { left: 0, right: 3, messages: ['A'] },
        { left: 3, right: 6, messages: ['A', 'B'] },
        { left: 6, right: 10, messages: ['A'] },
        { left: 12, right: 20, messages: ['C'] }
    ]);
    assert.deepEqual(warningSegments([], 0, 20), []);
});
test('overlapping reminders render disjoint single-layer badges with complete accessible details after redraws', async () => {
    const ui = setup({ slots: [shift(0, 12), shift(6, 16), shift(16, 20, [])], thresholds: { minimumRest: '8', maximumContinuous: '10', minimumStaff: '2' } }); await ui.flush();
    const lane = ui.get('warning-lane');
    const check = () => {
        let right = 0, multiple = false;
        for (const marker of lane.children) {
            const left = parseFloat(marker.style.left), width = parseFloat(marker.style.width);
            assert(left + 0.000001 >= right); right = left + width;
            assert.equal(marker.style.top, undefined);
            const messages = marker.dataset.summary.split('\n');
            assert.equal(marker.querySelector('.ac-gantt-warning-count').textContent, String(new Set(messages).size));
            for (const message of messages) { assert(marker.title.includes(message)); assert(marker.getAttribute('aria-label').includes(message)); }
            multiple ||= messages.length > 1;
        }
        assert(multiple);
        assert.equal(lane.style.minHeight, undefined, 'warning lane must use its fixed CSS height, never pack');
    };
    check();
    const marker = lane.children.find(item => Number(item.querySelector('.ac-gantt-warning-count').textContent) > 1);
    for (const event of ['pointerover', 'focusin', 'click']) {
        await ui.fire(marker, event); assert.equal(ui.get('shift-summary').textContent, marker.dataset.summary);
    }
    await ui.fire(ui.bar(1), 'keydown', { key: 'ArrowRight', shiftKey: true }); check();
    await ui.preview(); check();
    await ui.click('undo-schedule'); check();
});
for (const mode of ['move', 'start', 'end', 'handover']) {
    test(`${mode} pointer gesture preserves every lane and unrelated bar until release repacks a real overlap`, async () => {
        const ui = setup({ slots: [shift(0, 8), shift(12, 20), shift(8, 24, ['b'])], thresholds: { minimumRest: '8', minimumStaff: '2' } }); await ui.flush();
        const heights = () => ui.chart.querySelectorAll('.ac-gantt-lane').map(lane => lane.style.minHeight);
        const originalHeights = heights(), warningMarkers = [...ui.get('warning-lane').children];
        const stationary = ui.bar(mode === 'start' ? 0 : 1), stationaryTop = stationary.style.top;
        let layouts = 0; ui.chart.addEventListener('oncall-packed', () => layouts++);
        const handle = mode === 'handover' ? ui.get('handover-lane').children[0] : mode === 'move' ? ui.bar() : ui.bar(mode === 'start' ? 1 : 0).querySelector(`[data-resize="${mode}"]`);
        const origin = mode === 'move' ? 4 : mode === 'start' ? 12 : 8;
        const destination = mode === 'move' ? 14 : mode === 'start' ? 4 : 16;
        await ui.pointer('pointerdown', handle, origin);
        for (const time of [destination, origin, destination]) {
            await ui.pointer('pointermove', mode === 'handover' ? ui.get('handover-lane') : ui.lane('a'), time);
            assert.deepEqual(heights(), originalHeights);
            assert.deepEqual(ui.get('warning-lane').children, warningMarkers);
            assert.equal(ui.bar(mode === 'start' ? 0 : 1), stationary);
            assert.equal(stationary.style.top, stationaryTop);
            assert.equal(layouts, 0);
        }
        const moving = ui.bar(mode === 'start' ? 1 : 0);
        assert.equal(moving.dataset[mode === 'start' || mode === 'move' ? 'start' : 'end'],
            localTime(new Date(Date.parse(start) + (mode === 'move' ? 10 : destination) * hour), 'Asia/Shanghai'));
        await ui.pointer('pointerup', mode === 'handover' ? ui.get('handover-lane') : ui.lane('a'), destination);
        assert(layouts > 0);
        assert.equal(ui.lane('a').style.minHeight, '144px');
        assert.equal(ui.get('warning-lane').style.minHeight, originalHeights[0]);
        await ui.click('undo-schedule'); assert.deepEqual(heights(), originalHeights);
    });
}
test('new and transferred shifts keep destination lanes fixed until the gesture is committed', async () => {
    for (const mode of ['create', 'transfer', 'week']) {
        const ui = setup({ slots: [shift(0, 16), shift(4, 12, ['b'])], context: [{ ...shift(4, 12, ['b']), external: true }] }); await ui.flush();
        const heights = () => ui.chart.querySelectorAll('.ac-gantt-lane').map(lane => lane.style.minHeight);
        const before = heights(), marker = ui.get('warning-lane').children[0];
        const handle = mode === 'create' ? ui.lane('b') : mode === 'week' ? ui.rows.get('a').querySelector('[data-week-user]') : ui.bar();
        await ui.pointer('pointerdown', handle, 2);
        await ui.pointer('pointermove', ui.lane('b'), 8, { clientY: 150 });
        assert.deepEqual(heights(), before); assert.equal(ui.get('warning-lane').children[0], marker);
        assert(ui.bar(mode === 'create' ? 2 : 0, 'b'));
        await ui.pointer('pointerup', ui.lane('b'), 8, { clientY: 150 });
        assert.equal(ui.lane('b').style.minHeight, mode === 'week' ? '144px' : '216px');
        await ui.click('undo-schedule'); assert.deepEqual(heights(), before);
    }
});
test('expanded context retains position mapping and heights while dragging across overlaps', async () => {
    const ui = setup({ slots: [shift(0, 8), shift(12, 20)] }); await ui.flush();
    await ui.click(ui.toggles.previous); await ui.click(ui.toggles.next);
    const width = ui.bar().style.width, height = ui.lane('a').style.minHeight;
    await ui.pointer('pointerdown', ui.bar(), 4);
    await ui.pointer('pointermove', ui.lane('a'), 14);
    assert.equal(ui.lane('a').style.minHeight, height);
    assert.equal(ui.bar().style.width, width);
    assert(Math.abs(parseFloat(ui.bar().style.left) - 46 / 240 * 100) < 0.000001);
    await ui.pointer('pointerup', ui.lane('a'), 14);
    assert.equal(ui.lane('a').style.minHeight, '144px');
    assert(Math.abs(parseFloat(ui.bar().style.left) - 46 / 240 * 100) < 0.000001);
});
test('pointer cancellation and a no-op release both restore packed layout without adding undo history', async () => {
    const ui = setup({ slots: [shift(0, 16), shift(8, 24)] }); await ui.flush();
    for (const finish of ['pointercancel', 'lostpointercapture', 'Escape', 'pointerup']) {
        const original = ui.draft(), height = ui.lane('a').style.minHeight;
        await ui.pointer('pointerdown', ui.bar(), 4);
        await ui.pointer('pointermove', ui.lane('a'), 32);
        assert.equal(ui.lane('a').style.minHeight, height);
        if (finish === 'pointerup') await ui.pointer('pointermove', ui.lane('a'), 4);
        if (finish === 'Escape') await ui.fire(ui.chart, 'keydown', { key: 'Escape' });
        else await ui.pointer(finish, ui.lane('a'), 4);
        assert.deepEqual(ui.draft(), original);
        assert.equal(ui.lane('a').style.minHeight, '144px');
        assert.equal(ui.bar(1).style.top, '80px');
        assert.equal(ui.get('undo-schedule').disabled, true);
        assert.equal(ui.captures.size, 0);
    }
});

test('all seven warning types recompute from live edits before server preview and expose accessible hover/focus details', async () => {
    const ui = setup({ slots: [shift(0, 12), shift(6, 16), shift(16, 20, []), shift(22, 26, ['gone'])], members: [...members, { id: 'gone', name: 'Former engineer', departed: true }], thresholds: { minimumRest: '8', maximumContinuous: '10', minimumStaff: '2' } });
    await ui.flush();
    for (const message of Object.values(warningText)) assert(warnings(ui).some(value => value.includes(message)), message);
    const marker = ui.get('warning-lane').children[0];
    assert.equal(marker.tagName, 'BUTTON'); assert.equal(marker.getAttribute('aria-describedby'), 'oncall-shift-detail'); assert(marker.getAttribute('aria-label').includes('Asia/Shanghai'));
    await ui.fire(marker, 'pointerover'); assert.equal(ui.get('shift-summary').textContent, marker.dataset.summary);
    marker.focus(); assert.equal(ui.get('shift-start').textContent, marker.dataset.start); assert.equal(ui.get('shift-end').textContent, marker.dataset.end);
    while (ui.draft().length) await ui.click(ui.get('shift-rows').children[0].children[3].children[0]);
    await ui.drag(ui.lane('a'), 0, 168); await ui.check(ui.get('shift-rows').children[0].children[2], 'b', true);
    const after = warnings(ui);
    for (const key of ['gap', 'overlap', 'unassigned-warning', 'rest-warning', 'staff-warning', 'departed-warning']) assert(!after.some(value => value.includes(warningText[key])), key);
    assert(after.some(value => value.includes(warningText['continuous-warning'])));
    await ui.click(ui.get('shift-rows').children[0].children[3].children[0]);
    assert(!warnings(ui).some(value => value.includes(warningText['continuous-warning'])));
    assert.equal(ui.savedWarning.hidden, true); assert.equal(ui.get('published-status').hidden, true);
});
test('cross-team and adjacent duty trigger live overlap, rest and continuous warnings without adding hours', async () => {
    const ui = setup({ slots: [shift(0, 8), shift(12, 16)], context: [{ ...shift(-8, 0), external: true }, { ...shift(15, 20), external: true }], thresholds: { minimumRest: '8', maximumContinuous: '12' } }); await ui.flush();
    for (const key of ['overlap', 'rest-warning', 'continuous-warning']) assert(warnings(ui).some(value => value.includes(warningText[key])), key);
    assert.equal(ui.get('total-hours').textContent, '12.00 hours');
    await ui.input(tableInput(ui, 0, 'start'), '2026-10-12T08:00');
    await ui.input(tableInput(ui, 0, 'end'), '2026-10-12T09:00');
    await ui.click(ui.get('shift-rows').children[1].children[3].children[0]);
    assert(!warnings(ui).some(value => value.includes(warningText['continuous-warning'])));
});
test('continuous warning also includes external duty immediately after the draft shift', () => {
    const values = reminders([shift(0, 8)], [{ ...shift(8, 16), external: true }], Date.parse(start), Date.parse(end), members, { continuous: 12 }, 'Asia/Shanghai');
    assert(values.some(warning => warning.key === 'continuous-warning' && warning.person === 'a'));
});
test('warning API response adds server diagnostics without suppressing live validation', async () => {
    const ui = setup({ slots: [shift(0, 8, [])], serverWarnings: [{ startsAtUtc: start, endsAtUtc: end, message: 'Server policy warning' }] }); await ui.flush();
    assert(warnings(ui).includes('Server policy warning')); assert(warnings(ui).includes(warningText['unassigned-warning']));
    await ui.check(ui.get('shift-rows').children[0].children[2], 'a', true);
    assert(!warnings(ui).includes(warningText['unassigned-warning']));
    await ui.preview(); assert(warnings(ui).includes('Server policy warning')); assert(!warnings(ui).includes(warningText['unassigned-warning']));
});

test('copy repeats the entire unsaved multi-person draft into an arbitrary partial range and only stages before Save', async () => {
    const ui = setup({ slots: [shift(0, 24, ['a', 'b']), shift(24, 168, [])] }); await ui.flush();
    await ui.input(tableInput(ui, 0, 'end'), '2026-10-13T00:17');
    await ui.input(ui.get('range-start'), '2026-10-25T10:13'); await ui.input(ui.get('range-end'), '2026-11-03T10:17');
    await ui.click('copy-range');
    const copy = ui.request('/preview?generate=false')[0];
    assert.equal(copy.body.slots.length, 4); assert.deepEqual(copy.body.slots[0].userIds, ['a', 'b']); assert.deepEqual(copy.body.slots[1].userIds, []);
    assert.equal(copy.body.slots[0].startLocal, '2026-10-25T10:13'); assert.equal(copy.body.slots[0].endLocal, '2026-10-26T10:30');
    assert.equal(copy.body.slots[3].endLocal, '2026-11-03T10:17');
    assert.equal(ui.get('copy-drafts').children.length, 1); assert.equal(ui.request('/save').length, 0); assert.equal(ui.window.reloads, 0);
    assert.equal(ui.draft()[0].endLocal, '2026-10-13T00:17'); assert.equal(ui.root.dataset.version, 'version');
    assert.equal(ui.toggles.next.getAttribute('aria-expanded'), 'true');
    await ui.click('undo-schedule'); assert.equal(ui.get('copy-drafts').children.length, 0); assert.equal(ui.draft()[0].endLocal, '2026-10-13T00:17');
    await ui.click('redo-schedule'); assert.equal(ui.get('copy-drafts').children.length, 1);
    await ui.click('reset-schedule'); assert.equal(ui.get('copy-drafts').children.length, 0); assert.equal(ui.draft()[0].endLocal, '2026-10-13T00:00');
    await ui.click('undo-schedule'); assert.equal(ui.get('copy-drafts').children.length, 1); assert.equal(ui.draft()[0].endLocal, '2026-10-13T00:17');
});
test('copy replacement confirmation can cancel and is invalidated by destination or source edits', async () => {
    const ui = setup({ replacedSlots: [shift(168, 180, ['a'])] }); await ui.flush();
    await ui.click('copy-range'); assert.equal(ui.get('replace-confirm').hidden, false); assert.match(ui.get('replaced-shifts').textContent, /Alice <team>/);
    await ui.click('replace-cancel'); await ui.click('replace-apply'); assert.equal(ui.get('copy-drafts').children.length, 0);
    await ui.click('copy-range'); await ui.input(ui.get('range-end'), '2026-10-27T00:00'); await ui.click('replace-apply'); assert.equal(ui.get('copy-drafts').children.length, 0);
    await ui.click('copy-range'); await ui.input(tableInput(ui, 0, 'end'), '2026-10-16T22:17'); await ui.click('replace-apply'); assert.equal(ui.get('copy-drafts').children.length, 0);
    await ui.click('copy-range'); await ui.click('replace-apply'); assert.equal(ui.get('copy-drafts').children.length, 1); assert.equal(ui.get('replace-confirm').hidden, true);
    assert.equal(ui.request('/save').length, 0); assert.equal(ui.window.reloads, 0);
});
test('template generation stages confirmed draft only; cancelling and undo never write', async () => {
    const ui = setup({ replacedSlots: [shift(168, 180)] }); await ui.flush();
    await ui.click('generate'); assert.equal(ui.get('replace-confirm').hidden, false); await ui.click('replace-cancel'); assert.equal(ui.get('copy-drafts').children.length, 0);
    await ui.click('generate'); await ui.click('replace-apply'); assert.equal(ui.get('copy-drafts').children.length, 1);
    assert.equal(ui.request('/generate').length, 0); assert.equal(ui.request('/save').length, 0);
    await ui.click('undo-schedule'); assert.equal(ui.get('copy-drafts').children.length, 0);
    await ui.click('redo-schedule'); await ui.click('save-schedule'); assert.equal(ui.request('/save').length, 1);
    assert(ui.request('/save')[0].body.slots.some(slot => slot.userIds.includes('b')));
});
test('copy rejects invalid or partially overlapping ranges without changing the draft', async () => {
    const ui = setup(); await ui.flush();
    await ui.input(ui.get('range-start'), '2026-10-20T00:00'); await ui.input(ui.get('range-end'), '2026-10-19T00:00');
    await ui.click('copy-range'); assert.equal(ui.request('/preview?generate=false').length, 0); assert(ui.get('schedule-errors').children.length);
    await ui.input(ui.get('range-start'), '2026-10-18T00:00'); await ui.input(ui.get('range-end'), '2026-10-20T00:00');
    await ui.click('copy-range'); assert.equal(ui.get('copy-drafts').children.length, 0); assert.match(ui.get('schedule-errors').textContent, /overlap/);
});
test('one atomic save merges staged copies and untouched intervening saved ranges after a same-version GET', async () => {
    const middle = shift(180, 190, ['b'], 'middle'), replaced = shift(336, 350, ['a'], 'replaced'), external = { ...shift(180, 184, ['a'], 'other-team'), external: true };
    const ui = setup({ slots: [shift(0, 24, ['a'], 'original')], savedSlots: [shift(0, 24, ['a'], 'original'), middle, replaced], context: [external] }); await ui.flush();
    await ui.input(ui.get('range-start'), '2026-10-26T00:00'); await ui.input(ui.get('range-end'), '2026-11-02T00:00'); await ui.click('copy-range');
    assert.equal(ui.request('/save').length, 0);
    await ui.click('save-schedule');
    const saves = ui.request('/save'); assert.equal(saves.length, 1);
    const get = ui.requests.find(request => request.url.startsWith('/saved?')); assert(get); assert.equal(get.options.body, undefined);
    const query = new URL(get.url, 'https://example.invalid').searchParams;
    assert.equal(query.get('startUtc'), '2026-10-11T16:00:00.000Z'); assert.equal(query.get('endUtc'), '2026-11-01T16:00:00.000Z');
    const saved = saves[0].body;
    assert.equal(saved.version, 'version'); assert.equal(saved.startUtc, '2026-10-11T16:00:00.000Z'); assert.equal(saved.endUtc, '2026-11-01T16:00:00.000Z');
    assert.deepEqual(saved.slots.find(slot => slot.id === 'middle'), { ...middle, source: 0 }); assert(!saved.slots.some(slot => ['replaced', 'other-team'].includes(slot.id)));
    assert.equal(saved.slots.length, 3); assert.equal(ui.window.reloads, 1);
});
test('expanded-save version conflict prevents every write and keeps all staged drafts and navigation protection', async () => {
    const ui = setup({ savedVersion: 'concurrently-updated' }); await ui.flush(); await ui.click('copy-range'); await ui.click('save-schedule');
    assert.equal(ui.request('/save').length, 0); assert.equal(ui.window.reloads, 0); assert.equal(ui.get('copy-drafts').children.length, 1);
    assert(ui.get('schedule-errors').children.length); assert.equal(ui.root.inert, false); assert.equal(ui.chart.inert, false);
    const event = await ui.fire(ui.window, 'beforeunload'); assert(event.defaultPrevented);
});
test('failed save retains exact edits and ranges, exposes errors and permits retry', async () => {
    const options = { fail: true }; const ui = setup(options); await ui.flush();
    await ui.input(tableInput(ui, 0, 'end'), '2026-10-16T22:17'); await ui.click('copy-range'); const draft = ui.draft();
    await ui.click('save-schedule'); assert.equal(ui.window.reloads, 0); assert.deepEqual(ui.draft(), draft); assert.equal(ui.get('copy-drafts').children.length, 1);
    assert.equal(ui.get('schedule-errors').textContent, 'Version conflict'); assert.equal(ui.root.inert, false);
    const event = await ui.fire(ui.window, 'beforeunload'); assert(event.defaultPrevented);
    options.fail = false; await ui.click('save-schedule'); assert.equal(ui.request('/save').length, 2); assert.equal(ui.window.reloads, 1);
});
test('late preview response cannot replace a newer draft or warnings', async () => {
    const ui = setup({ deferPreview: true }); await ui.flush(); assert.equal(ui.deferred.length, 1);
    await ui.input(tableInput(ui, 0, 'end'), '2026-10-16T22:17'); await ui.preview(); assert.equal(ui.deferred.length, 2);
    const response = message => ({ version: 'version', startUtc: start, endUtc: end, slots: initial, replacedSlots: [], warnings: [message], timelineWarnings: [{ startsAtUtc: start, endsAtUtc: end, message }] });
    ui.deferred[1].resolve(response('Latest preview')); await ui.flush(); assert(warnings(ui).includes('Latest preview'));
    ui.deferred[0].resolve(response('Stale preview')); await ui.flush();
    assert(!warnings(ui).includes('Stale preview')); assert(warnings(ui).includes('Latest preview')); assert.equal(ui.draft()[0].endLocal, '2026-10-16T22:17');
});
test('popup refuses skipped DST times and unchanged repeated-time saves retain the original UTC instant', async () => {
    const spring = setup({ zone: 'America/New_York', start: '2026-03-02T05:00:00Z', end: '2026-03-09T04:00:00Z', slots: [{ startsAtUtc: '2026-03-08T06:00:00Z', endsAtUtc: '2026-03-08T08:00:00Z', userIds: ['a'] }] }); await spring.flush();
    await spring.click(spring.bar()); spring.get('dialog-start').value = '2026-03-08T02:30'; await spring.click('dialog-apply');
    assert(spring.get('shift-dialog').open); assert(spring.get('dialog-errors').children.length); assert.equal(spring.draft()[0].startLocal, '2026-03-08T01:00');
    const fall = setup({ zone: 'America/New_York', start: '2026-10-26T04:00:00Z', end: '2026-11-02T05:00:00Z', slots: [{ startsAtUtc: '2026-11-01T05:30:00Z', endsAtUtc: '2026-11-01T07:30:00Z', userIds: ['a'] }] }); await fall.flush();
    await fall.click(fall.bar()); await fall.check(fall.get('dialog-members'), 'b', true); await fall.click('dialog-apply'); await fall.click('save-schedule');
    const saved = fall.request('/save')[0].body.slots[0]; assert.equal(saved.startsAtUtc, '2026-11-01T05:30:00Z'); assert.equal(saved.startLocal, undefined);
    assert.equal(saved.endsAtUtc, '2026-11-01T07:30:00Z'); assert.equal(fall.get('total-hours').textContent, '2.00 hours');
});
test('localized warning dictionary, accessible hints and duration units use localized labels', async () => {
    const ui = setup({ slots: [shift(0, 8, [])], messages: { gap: '排班存在空缺', 'unassigned-warning': '未安排值班人员', 'oncall-hours-unit': '小时', hours: '{0} 小时', 'move-hint': '方向键移动；回车编辑', 'resize-start': '调整开始时间', start: '开始', 'draft-range': '草稿范围' } }); await ui.flush();
    assert(warnings(ui).includes('排班存在空缺')); assert(warnings(ui).includes('未安排值班人员')); assert.equal(ui.get('total-hours').textContent, '0.00 小时');
    assert.match(ui.bar(0, '').getAttribute('aria-label'), /方向键移动；回车编辑/);
    assert.equal(ui.bar(0, '').querySelector('[data-resize="start"]').title, '调整开始时间'); assert.equal(tableInput(ui, 0, 'start').getAttribute('aria-label'), '开始');
    await ui.check(ui.get('shift-rows').children[0].children[2], 'a', true); assert.equal(ui.rows.get('a').querySelector('.ac-gantt-hours').textContent, '1 · 8.00 小时');
    await ui.click('copy-range'); assert.match(ui.get('copy-drafts').textContent, /草稿范围/);
    const feedback = fs.readFileSync(require.resolve('../../src/Aiursoft.AlertCenter/Views/Shared/_ActionFeedback.cshtml'), 'utf8');
    assert.match(feedback, /data-key="oncall-hours-unit">@viewLocalizer\["hours"\]/);
});
test('precise table and bulk tools are default-collapsed markup using the same draft, with semantic dialog and warning text', () => {
    const markup = fs.readFileSync(require.resolve('../../src/Aiursoft.AlertCenter/Views/OnCall/_ScheduleEditor.cshtml'), 'utf8');
    const details = markup.match(/<details\b[^>]*>/g); assert(details.length >= 2); assert(details.every(tag => !/\bopen\b/.test(tag)));
    assert.match(markup, /<summary[^>]*>@viewLocalizer\["Precise editing \/ bulk operations"\]<\/summary>/);
    assert.match(markup, /<tbody id="oncall-shift-rows"/); assert.match(markup, /<dialog id="oncall-shift-dialog"[^>]*aria-labelledby=/);
    assert.match(markup, /id="oncall-dialog-members"[^>]*aria-label=/);
    for (const key of Object.keys(warningText)) assert(markup.includes(`data-schedule-text="${key}"`), key);
    assert.match(markup, /data-get-url=/); assert.match(markup, /data-context=/);
    let depth = 0;
    for (const match of markup.matchAll(/<details\b[^>]*>|<\/details>|<div id="(oncall-replace-confirm|oncall-clear-history-confirm)"/g)) {
        if (match[0].startsWith('<details')) depth++;
        else if (match[0] === '</details>') depth--;
        else assert.equal(depth, 0, `${match[1]} must remain visible when bulk tools are collapsed`);
    }
});

test('week row buttons support pointer dragging and click selection followed by a target row, including cancellation', async () => {
    const ui = setup({ slots: [shift(0, 12), shift(12, 24, ['b'])], context: [{ ...shift(24, 30), external: true }] }); await ui.flush();
    const original = ui.draft(); const week = id => ui.rows.get(id).querySelector('[data-week-user]');
    await ui.drag(week('a'), 0, 4, ui.lane('b'));
    assert.deepEqual(ui.draft().map(slot => slot.userIds), [['b'], ['a']]);
    await ui.click('undo-schedule'); assert.deepEqual(ui.draft(), original);
    await ui.click(week('a')); assert.equal(week('a').getAttribute('aria-pressed'), 'true');
    await ui.click(ui.readonly[0]); assert.deepEqual(ui.draft(), original);
    await ui.click(ui.lane('b')); assert.deepEqual(ui.draft().map(slot => slot.userIds), [['b'], ['a']]);
    await ui.click('undo-schedule');
    await ui.click(week('a')); await ui.fire(ui.chart, 'keydown', { key: 'Escape' });
    assert.equal(week('a').getAttribute('aria-pressed'), 'false');
    await ui.click(ui.lane('b')); assert.deepEqual(ui.draft(), original);
    await ui.drag(week('a'), 0, 4, ui.readonly[0]); assert.deepEqual(ui.draft(), original);
});
test('selected shift moves through the precise controls and legacy blank click while retaining duration', async () => {
    const ui = setup({ slots: [shift(12, 20)] }); await ui.flush();
    await ui.click('move-selected'); assert(ui.get('schedule-errors').children.length);
    await ui.click(ui.bar()); await ui.click('dialog-cancel');
    ui.get('move-member').value = 'b'; ui.get('move-start').value = '2026-10-13T04:17'; await ui.click('move-selected');
    assert.deepEqual(ui.draft()[0], { startLocal: '2026-10-13T04:17', endLocal: '2026-10-13T12:17', userIds: ['b'] });
    await ui.pointer('pointerdown', ui.lane('a'), 40 + 13 / 60, { altKey: true });
    await ui.pointer('pointerup', ui.lane('a'), 40 + 13 / 60, { altKey: true });
    assert.deepEqual(ui.draft()[0], { startLocal: '2026-10-13T16:13', endLocal: '2026-10-14T00:13', userIds: ['a'] });
    assert.equal(ui.request('/save').length, 0);
});
test('staged next-range bars remain read-only and participate in undo without contributing current-week statistics', async () => {
    const ui = setup({ slots: [shift(0, 8)] }); await ui.flush(); await ui.click('copy-range');
    const staged = ui.lane('a').querySelector('[data-edge="next"]'); assert(staged); assert.equal(staged.dataset.readonly, 'true'); assert.equal(staged.hidden, false);
    const original = ui.draft(); await ui.drag(staged, 170, 172, ui.lane('b')); await ui.click(staged); await ui.fire(staged, 'keydown', { key: 'Delete' });
    assert.deepEqual(ui.draft(), original); assert(!ui.get('shift-dialog').open); assert.equal(ui.get('copy-drafts').children.length, 1);
    assert.equal(ui.get('total-hours').textContent, '8.00 hours'); assert.equal(ui.get('total-shifts').textContent, '1');
    assert.equal(ui.rows.get('a').querySelector('.ac-gantt-hours').textContent, '1 · 8.00 hours');
    await ui.click('undo-schedule'); assert.equal(ui.lane('a').querySelector('[data-edge="next"]'), null);
});
test('short pointer taps open a shift once, while foreign pointer ids and lost capture cannot commit a drag', async () => {
    const ui = setup({ slots: [shift(12, 20)] }); await ui.flush(); const original = ui.draft();
    await ui.pointer('pointerdown', ui.bar(), 14); await ui.pointer('pointerup', ui.bar(), 14);
    assert(ui.get('shift-dialog').open); assert.deepEqual(ui.draft(), original); assert.equal(ui.get('undo-schedule').disabled, true);
    await ui.fire(ui.bar(), 'click', { detail: 1 }); await ui.click('dialog-cancel');
    await ui.pointer('pointerdown', ui.bar(), 14, { pointerId: 7 });
    await ui.pointer('pointermove', ui.lane('b'), 18, { pointerId: 8, clientY: 140 });
    await ui.pointer('pointerup', ui.lane('b'), 18, { pointerId: 8 }); assert.deepEqual(ui.draft(), original);
    await ui.pointer('pointermove', ui.lane('b'), 18, { pointerId: 7, clientY: 140 });
    await ui.fire(ui.chart, 'lostpointercapture', { pointerId: 7 }); await ui.pointer('pointerup', ui.lane('b'), 18, { pointerId: 7 });
    assert.deepEqual(ui.draft(), original); assert.equal(ui.bar(0, 'b'), null); assert.equal(ui.get('undo-schedule').disabled, true);
});
test('copy over the visible week replaces only after confirmation and undo restores the shared draft', async () => {
    const ui = setup({ slots: [shift(12, 20)] }); await ui.flush(); const original = ui.draft();
    await ui.input(ui.get('range-start'), '2026-10-12T00:00'); await ui.input(ui.get('range-end'), '2026-10-19T00:00');
    await ui.click('generate'); assert.equal(ui.get('replace-confirm').hidden, false); assert.deepEqual(ui.draft(), original);
    await ui.click('replace-apply'); assert.deepEqual(ui.draft(), [{ startLocal: '2026-10-12T00:00', endLocal: '2026-10-19T00:00', userIds: ['b'] }]);
    assert(ui.bar(0, 'b')); assert.equal(ui.get('copy-drafts').children.length, 0); assert.equal(ui.request('/save').length, 0);
    await ui.click('undo-schedule'); assert.deepEqual(ui.draft(), original); assert(ui.bar());
});
test('continuous warnings include a following external chain and clear when a live edit breaks that chain', async () => {
    const ui = setup({ slots: [shift(0, 8)], context: [{ ...shift(8, 12), external: true }, { ...shift(12, 16), external: true }], thresholds: { maximumContinuous: '12' } }); await ui.flush();
    assert(warnings(ui).some(value => value.includes(warningText['continuous-warning'])));
    await ui.input(tableInput(ui, 0, 'end'), '2026-10-12T07:30');
    assert(!warnings(ui).some(value => value.includes(warningText['continuous-warning'])));
    await ui.click('undo-schedule'); assert(warnings(ui).some(value => value.includes(warningText['continuous-warning'])));
});
test('copy replacement never hides external context from rest validation and invalidates overlapping staged destinations', async () => {
    const ui = setup({ slots: [shift(156, 168)], context: [{ ...shift(168, 176), external: true }], thresholds: { minimumRest: '8' } }); await ui.flush();
    assert(warnings(ui).some(value => value.includes(warningText['rest-warning'])));
    await ui.click('copy-range'); assert.equal(ui.get('copy-drafts').children.length, 1);
    assert(warnings(ui).some(value => value.includes(warningText['rest-warning']))); assert(ui.readonly[0].parentElement);
    await ui.input(ui.get('range-start'), '2026-10-25T00:00'); await ui.input(ui.get('range-end'), '2026-11-01T00:00');
    await ui.click('copy-range'); assert.equal(ui.get('copy-drafts').children.length, 1); assert.match(ui.get('schedule-errors').textContent, /overlap/);
});
test('cross-week shift renders read-only adjacent fragments and saves expanded bounds without duplicating its saved id', async () => {
    const crossing = shift(-2, 8, ['a'], 'crossing'), adjacent = shift(-8, -3, ['b'], 'untouched');
    const ui = setup({ slots: [crossing], savedSlots: [crossing, adjacent] }); await ui.flush();
    assert.equal(ui.bar().querySelector('[data-resize="start"]'), null);
    await ui.click(ui.toggles.previous); const fragment = ui.lane('a').querySelector('[data-edge="previous"]');
    assert(fragment); assert.equal(fragment.dataset.readonly, 'true'); assert.equal(fragment.hidden, false);
    await ui.fire(fragment, 'keydown', { key: 'Delete' }); assert.equal(ui.draft().length, 1);
    await ui.input(tableInput(ui, 0, 'end'), '2026-10-12T09:00'); await ui.click('save-schedule');
    const saved = ui.request('/save')[0].body;
    assert.equal(saved.startUtc, '2026-10-11T14:00:00.000Z'); assert.equal(saved.slots.filter(slot => slot.id === 'crossing').length, 1);
    assert.equal(saved.slots.find(slot => slot.id === 'crossing').endLocal, '2026-10-12T09:00');
});
test('expanded blank adjacent context cannot start creation or receive a moved or newly created shift', async () => {
    const ui = setup({ slots: [shift(12, 20)] }); await ui.flush();
    await ui.click(ui.toggles.previous); await ui.click(ui.toggles.next); const original = ui.draft();
    for (const outside of [-4, 168.5, 172]) {
        await ui.drag(ui.lane('b'), outside, 30, ui.lane('b'));
        assert.deepEqual(ui.draft(), original, `Creation cannot begin outside the current week at ${outside}`);
        await ui.drag(ui.lane('b'), 30, outside, ui.lane('b'));
        assert.deepEqual(ui.draft(), original, `Creation cannot finish outside the current week at ${outside}`);
        await ui.drag(ui.bar(), 14, outside, ui.lane('b'));
        assert.deepEqual(ui.draft(), original, `Existing shift cannot target adjacent blank space at ${outside}`);
    }
    await ui.drag(ui.lane('b'), 168, 30, ui.lane('b'));
    assert.deepEqual(ui.draft(), original, 'Creation cannot begin at the exclusive end boundary');
    await ui.drag(ui.bar(), 14, 168, ui.lane('b'));
    assert.deepEqual(ui.draft(), original, 'Existing shifts cannot target the exclusive end boundary');
    assert.equal(ui.get('undo-schedule').disabled, true); assert.equal(ui.request('/save').length, 0);
    await ui.click(ui.bar()); await ui.click('dialog-cancel');
    for (const outside of [168, 172]) {
        await ui.pointer('pointerdown', ui.lane('b'), outside); await ui.pointer('pointerup', ui.lane('b'), outside);
        assert.deepEqual(ui.draft(), original);
    }
});
test('blank creation may end exactly at the week boundary with adjacent context expanded', async () => {
    const ui = setup({ slots: [] }); await ui.flush();
    await ui.click(ui.toggles.previous); await ui.click(ui.toggles.next);
    await ui.drag(ui.lane('b'), 0, 168, ui.lane('b'));
    assert.deepEqual(ui.draft(), [{ startLocal: '2026-10-12T00:00', endLocal: '2026-10-19T00:00', userIds: ['b'] }]);
    assert.equal(ui.get('total-hours').textContent, '168.00 hours');
    assert.equal(ui.lane('b').querySelector('[data-edge="next"]'), null); assert.equal(ui.request('/save').length, 0);
    await ui.click('undo-schedule'); assert.deepEqual(ui.draft(), []);
});
test('per-person statistics count intersecting shifts while clipping and merging overlapping hours', async () => {
    const ui = setup({
        slots: [shift(-4, 8), shift(4, 12, ['a', 'b']), shift(12, 16), shift(-8, 0), shift(168, 176), shift(20, 24, [])],
        context: [{ ...shift(0, 24, ['a']), external: true }, shift(-12, -4, ['a'])]
    }); await ui.flush();
    const stats = id => ui.rows.get(id).querySelector('.ac-gantt-hours').textContent;
    assert.equal(stats('a'), '3 · 16.00 hours');
    assert.equal(stats('b'), '1 · 8.00 hours');
    assert.equal(stats(''), '0 · 0.00 hours');
    await ui.click(ui.get('shift-rows').children[1].children[3].children[0]);
    assert.equal(stats('a'), '2 · 12.00 hours'); assert.equal(stats('b'), '0 · 0.00 hours');
    await ui.click('undo-schedule'); assert.equal(stats('a'), '3 · 16.00 hours'); assert.equal(stats('b'), '1 · 8.00 hours');
});

test('clear defaults use team now and round the final saved shift up, extending to later copied drafts', async () => {
    const ui = setup({ now: '2026-10-12T04:13:45.123Z', lastShiftEnd: '2026-10-20T09:17:22Z' }); await ui.flush();
    assert.equal(ui.get('clear-start').value, '2026-10-12T12:13');
    assert.equal(ui.get('clear-end').value, '2026-10-20T17:18');
    assert.equal(ui.get('clear-manual').checked, false); assert.equal(ui.get('clear-history').checked, false);
    assert.equal(ui.get('clear-auto-options').hidden, true);
    await ui.click('copy-range'); assert.equal(ui.get('clear-end').value, '2026-10-26T00:00');
    await ui.click('undo-schedule'); assert.equal(ui.get('clear-end').value, '2026-10-20T17:18');
    await ui.input(ui.get('clear-end'), '2026-10-21T00:00'); await ui.click('redo-schedule');
    assert.equal(ui.get('clear-end').value, '2026-10-21T00:00', 'Explicit end is not overwritten by another draft edit');
});
test('clear uses authoritative source and precise ongoing cutoff, updates Gantt, and remains undoable until one save', async () => {
    const generated = { ...shift(0, 24, ['a'], 'generated'), source: 1 };
    const manual = { ...shift(24, 48, ['b'], 'manual'), source: 0 };
    const cutoff = '2026-10-12T04:13:45.123Z';
    const kept = { ...generated, endsAtUtc: cutoff };
    const ui = setup({ now: cutoff, slots: [generated, manual], clearSlots: [kept, manual], serverNow: cutoff }); await ui.flush();
    const original = ui.draft();
    await ui.click('clear-range');
    const clear = ui.request('/preview?clear=true')[0];
    assert.equal(clear.options.method, 'POST'); assert.equal(clear.options.headers.RequestVerificationToken, 'csrf');
    assert.deepEqual(clear.body.slots.map(slot => slot.source), [1, 0]);
    assert.deepEqual(clear.body.clearRanges, [{ startUtc: '2026-10-12T04:13:00.000Z', endUtc: manual.endsAtUtc, includeManual: false, includeHistory: false, confirmHistory: false, autoExtendAction: null }]);
    assert.equal(ui.bar().dataset.end, '2026-10-12T12:13'); assert(ui.bar(1, 'b'));
    assert(warnings(ui).includes(warningText.gap)); assert.equal(ui.request('/save').length, 0);
    const cleared = ui.draft(); await ui.preview();
    const preview = ui.request('/preview').at(-1).body;
    assert.equal(preview.slots[0].endsAtUtc, cutoff); assert.equal(preview.slots[0].source, 1);
    assert.equal(preview.clearRanges[0].clearedAtUtc, cutoff);
    await ui.click('undo-schedule'); assert.deepEqual(ui.draft(), original); assert.equal(ui.get('copy-drafts').children.length, 0);
    await ui.click('redo-schedule'); assert.deepEqual(ui.draft(), cleared);
    await ui.click('reset-schedule'); assert.deepEqual(ui.draft(), original); assert.equal(ui.get('draft-notice').hidden, true);
    await ui.click('undo-schedule'); assert.deepEqual(ui.draft(), cleared);
    await ui.click('save-schedule'); assert.equal(ui.request('/save').length, 1);
    const saved = ui.request('/save')[0].body;
    assert.equal(saved.slots[0].endsAtUtc, cutoff); assert.equal(saved.slots[0].source, 1); assert.equal(saved.slots[1].source, 0);
    assert.equal(saved.clearRanges[0].clearedAtUtc, cutoff); assert.equal(saved.version, 'version');
    assert(ui.requests.filter(item => item.options.method === 'POST').every(item => item.url.startsWith('/preview') || item.url === '/save'));
});
test('malformed clear descriptors leave the draft and undo history intact; valid responses retain seven-digit UTC precision', async () => {
    const options = { now: start, slots: [{ ...shift(0, 24, ['a'], 'generated'), source: 1 }], clearResponse: {
        clearRanges: [{ StartUtc: '2026-10-11T16:00:00Z', EndUtc: '2026-10-12T16:00:00Z', IncludeManual: false, IncludeHistory: false, ConfirmHistory: false, AutoExtendAction: null, ClearedAtUtc: '2026-10-12T04:13:45.1234567Z' }],
        slots: [{ ...shift(0, 12, ['a'], 'generated'), endsAtUtc: '2026-10-12T04:13:45.1234567Z', source: 1 }]
    } };
    const ui = setup(options); await ui.flush(); const before = ui.draft();
    await ui.click('clear-range'); assert.match(ui.get('schedule-errors').textContent, /Unable to save schedule/);
    assert.deepEqual(ui.draft(), before); assert.equal(ui.get('undo-schedule').disabled, true); assert.equal(ui.get('draft-notice').hidden, true);
    options.clearResponse.clearRanges = [{ startUtc: '2026-10-11T16:00:00Z', endUtc: '2026-10-12T16:00:00Z', includeManual: false, includeHistory: false, confirmHistory: false, autoExtendAction: null, clearedAtUtc: '2026-10-12T04:13:45.1234567Z' }];
    await ui.click('clear-range'); assert.equal(ui.get('schedule-errors').textContent, '');
    assert.equal(ui.draft()[0].endLocal, '2026-10-12T12:13'); assert.equal(ui.bar().dataset.end, '2026-10-12T12:13');
    await ui.preview(); const preview = ui.request('/preview').at(-1).body;
    assert.equal(preview.clearRanges[0].clearedAtUtc, '2026-10-12T04:13:45.1234567Z');
    assert.equal(preview.slots[0].endsAtUtc, '2026-10-12T04:13:45.1234567Z');
    assert.equal(preview.clearRanges[0].startUtc, '2026-10-11T16:00:00Z');
    await ui.click('save-schedule'); assert.equal(ui.request('/save')[0].body.clearRanges[0].endUtc, '2026-10-12T16:00:00Z');
});
test('history clear requires a separate confirmation and changing scope invalidates it without touching the draft', async () => {
    const ui = setup({ slots: [{ ...shift(0, 24), source: 1 }], now: '2026-10-13T00:00:00Z' }); await ui.flush();
    await ui.input(ui.get('clear-start'), '2026-10-12T00:00');
    ui.get('clear-history').checked = true; await ui.fire(ui.get('clear-history'), 'change');
    ui.get('clear-manual').checked = true; await ui.fire(ui.get('clear-manual'), 'change');
    const before = ui.draft();
    await ui.click('clear-range'); assert.equal(ui.get('clear-history-confirm').hidden, false);
    assert.match(ui.get('clear-history-range').textContent, /Asia\/Shanghai/); assert.equal(ui.request('/preview?clear=true').length, 0);
    await ui.click('clear-history-cancel'); await ui.click('clear-history-apply'); assert.deepEqual(ui.draft(), before);
    await ui.click('clear-range'); await ui.input(ui.get('clear-end'), '2026-10-14T00:00');
    await ui.click('clear-history-apply'); assert.equal(ui.request('/preview?clear=true').length, 0);
    await ui.click('clear-range'); await ui.click('clear-history-apply');
    const operation = ui.request('/preview?clear=true')[0].body.clearRanges[0];
    assert.equal(operation.includeManual, true); assert.equal(operation.includeHistory, true); assert.equal(operation.confirmHistory, true);
    assert.equal(ui.get('clear-history-confirm').hidden, true); assert.equal(ui.draft().length, 0);
    await ui.click('undo-schedule'); assert.deepEqual(ui.draft(), before); assert.equal(ui.request('/save').length, 0);
});
test('automatic extension has no default action and disabling is submitted atomically with the clear on save', async () => {
    const ui = setup({ autoExtend: true, now: start, slots: [{ ...shift(0, 24), source: 1 }] }); await ui.flush();
    assert.equal(ui.get('clear-auto-options').hidden, false); assert.equal(ui.get('clear-auto-action').value, '');
    await ui.click('clear-range'); assert.equal(ui.request('/preview?clear=true').length, 0);
    assert.match(ui.get('schedule-errors').textContent, /disable automatic extension or regenerate/);
    ui.get('clear-auto-action').value = 'disable'; await ui.fire(ui.get('clear-auto-action'), 'change'); await ui.click('clear-range');
    assert.equal(ui.draft().length, 0); assert.equal(ui.get('replace-confirm').hidden, true); assert.equal(ui.request('/save').length, 0);
    await ui.click('undo-schedule'); assert.equal(ui.get('clear-auto-action').value, '');
    await ui.click('redo-schedule'); assert.equal(ui.get('clear-auto-action').value, 'disable');
    await ui.click('save-schedule');
    const saved = ui.request('/save')[0].body;
    assert.deepEqual(saved.slots, []); assert.equal(saved.clearRanges[0].autoExtendAction, 'disable'); assert.equal(saved.confirmReplace, undefined);
    assert.equal(ui.request('/generate').length, 0); assert.equal(ui.window.reloads, 1);
});
test('regenerate clear uses visible replacement confirmation, leaves a gap until save, and restores choices with undo', async () => {
    const ui = setup({ autoExtend: true, now: start, slots: [{ ...shift(0, 24), source: 1 }] }); await ui.flush();
    const before = ui.draft();
    ui.get('clear-auto-action').value = 'regenerate'; await ui.fire(ui.get('clear-auto-action'), 'change');
    await ui.click('clear-range');
    assert.equal(ui.get('replace-confirm').hidden, false); assert.equal(ui.get('replace-regenerate-hint').hidden, false);
    assert.match(ui.get('replaced-shifts').textContent, /Alice <team>/); assert.deepEqual(ui.draft(), before);
    await ui.click('replace-cancel'); await ui.click('replace-apply'); assert.deepEqual(ui.draft(), before);
    await ui.click('clear-range'); await ui.input(ui.get('clear-end'), '2026-10-14T00:00'); await ui.click('replace-apply'); assert.deepEqual(ui.draft(), before);
    await ui.click('clear-range'); await ui.click('replace-apply'); assert.deepEqual(ui.draft(), []);
    assert.equal(ui.bar(), null); assert(warnings(ui).includes(warningText.gap));
    assert.equal(ui.request('/generate').length, 0); assert.equal(ui.request('/save').length, 0);
    await ui.click('undo-schedule'); assert.deepEqual(ui.draft(), before); assert.equal(ui.get('clear-auto-action').value, '');
    await ui.click('redo-schedule'); assert.deepEqual(ui.draft(), []); assert.equal(ui.get('clear-auto-action').value, 'regenerate');
    await ui.click('save-schedule'); const saved = ui.request('/save')[0].body;
    assert.equal(saved.confirmReplace, true); assert.equal(saved.clearRanges[0].autoExtendAction, 'regenerate'); assert.deepEqual(saved.slots, []);
});
test('failed clear and version mismatch retain visible edits and noncontiguous copies without writes', async () => {
    const options = { now: start, failClear: true }; const ui = setup(options); await ui.flush();
    await ui.input(tableInput(ui, 0, 'end'), '2026-10-16T22:17'); await ui.click('copy-range'); const before = ui.draft();
    await ui.click('clear-range'); assert.equal(ui.get('schedule-errors').textContent, 'Clear rejected');
    assert.deepEqual(ui.draft(), before); assert.equal(ui.get('copy-drafts').children.length, 1); assert.equal(ui.root.inert, false);
    options.failClear = false; options.savedVersion = 'conflict'; await ui.click('clear-range');
    assert.equal(ui.request('/preview?clear=true').length, 1); assert.deepEqual(ui.draft(), before); assert.equal(ui.get('copy-drafts').children.length, 1);
    assert.equal(ui.request('/save').length, 0); assert((await ui.fire(ui.window, 'beforeunload')).defaultPrevented);
    options.savedVersion = 'version'; await ui.click('save-schedule'); assert.equal(ui.request('/save')[0].body.clearRanges.length, 0);
});
test('clear assembles noncontiguous drafts with untouched saved gaps and preserves crossing IDs exactly once', async () => {
    const original = { ...shift(-2, 24, ['a'], 'original'), source: 1 };
    const middle = { ...shift(180, 190, ['b'], 'middle'), source: 0 };
    const replaced = { ...shift(336, 350, ['a'], 'replaced'), source: 1 };
    const later = { ...shift(660, 680, ['b'], 'later'), source: 1 };
    const options = { now: start, slots: [original], savedSlots: [original, middle, replaced, later], lastShiftEnd: later.endsAtUtc };
    const ui = setup(options); await ui.flush();
    await ui.input(tableInput(ui, 0, 'end'), '2026-10-13T00:17');
    await ui.input(ui.get('range-start'), '2026-10-26T00:00'); await ui.input(ui.get('range-end'), '2026-11-02T00:00'); await ui.click('copy-range');
    const copied = ui.request('/preview?generate=false')[0].body.slots[0];
    options.clearSlots = [{ ...original, endsAtUtc: shift(0, 24 + 17 / 60).endsAtUtc, source: 0 }, middle, { startsAtUtc: '2026-10-25T16:00:00.000Z', endsAtUtc: '2026-10-26T16:17:00.000Z', userIds: copied.userIds, source: 0 }];
    await ui.input(ui.get('clear-start'), '2026-11-08T00:00'); await ui.click('clear-range');
    const clear = ui.request('/preview?clear=true')[0].body;
    assert.equal(clear.startUtc, original.startsAtUtc); assert.equal(clear.endUtc, later.endsAtUtc);
    assert.equal(clear.slots.filter(slot => slot.id === 'original').length, 1); assert.equal(clear.slots.find(slot => slot.id === 'original').endLocal, '2026-10-13T00:17');
    assert(clear.slots.some(slot => slot.id === 'middle')); assert(clear.slots.some(slot => slot.id === 'later'));
    assert(!clear.slots.some(slot => slot.id === 'replaced')); assert(clear.slots.some(slot => localTime(slot.startsAtUtc, 'Asia/Shanghai') === copied.startLocal));
    await ui.preview(); const after = ui.request('/preview').at(-1).body;
    assert.equal(after.slots.filter(slot => slot.id === 'original').length, 1); assert.equal(after.slots.length, 3);
    assert.equal(after.slots.find(slot => slot.id === 'original').source, 0);
    await ui.click('save-schedule'); const saved = ui.request('/save')[0].body;
    assert.equal(saved.slots.filter(slot => slot.id === 'original').length, 1); assert.equal(saved.slots.length, 3);
    assert(saved.slots.some(slot => slot.id === 'middle')); assert(!saved.slots.some(slot => slot.id === 'later' || slot.id === 'replaced'));
    assert.equal(saved.endUtc, later.endsAtUtc, 'Cleared empty tail still participates in saving bounds');
    assert.equal(saved.clearRanges.length, 1);
});
test('failed save retains a confirmed clear and its automatic-extension action for retry', async () => {
    const options = { now: start, autoExtend: true, fail: true }; const ui = setup(options); await ui.flush();
    ui.get('clear-auto-action').value = 'disable'; await ui.fire(ui.get('clear-auto-action'), 'change'); await ui.click('clear-range');
    await ui.click('save-schedule'); assert.equal(ui.window.reloads, 0); assert.equal(ui.get('clear-auto-action').value, 'disable');
    assert.deepEqual(ui.draft(), []); assert.match(ui.get('schedule-errors').textContent, /Version conflict/);
    const first = ui.request('/save')[0].body; assert.equal(first.clearRanges[0].autoExtendAction, 'disable');
    options.fail = false; await ui.click('save-schedule');
    assert.deepEqual(ui.request('/save')[1].body, first); assert.equal(ui.window.reloads, 1);
});
test('clear keeps server split fragments once across the view and preserves saved neighbors beyond crossing bounds', async () => {
    const crossing = { ...shift(156, 200, ['a'], 'crossing'), source: 1 };
    const long = { ...shift(300, 600, ['b'], 'long'), source: 0 };
    const neighbor = { ...shift(560, 570, ['a'], 'neighbor'), source: 0 };
    const fragments = [{ ...shift(156, 164, ['a'], 'crossing'), source: 1 }, { ...shift(180, 200), source: 1 }, long];
    const ui = setup({ now: start, slots: [crossing], savedSlots: [crossing, long, neighbor], clearSlots: fragments }); await ui.flush();
    await ui.input(ui.get('clear-start'), '2026-10-18T20:00'); await ui.input(ui.get('clear-end'), '2026-10-29T00:00'); await ui.click('clear-range');
    assert.equal(ui.draft().length, 1); assert.equal(ui.bar().dataset.end, '2026-10-18T20:00');
    await ui.click('save-schedule'); const saved = ui.request('/save')[0].body;
    assert.equal(saved.slots.filter(slot => slot.id === 'crossing').length, 1);
    assert.equal(saved.slots.filter(slot => slot.id === 'long').length, 1);
    assert(saved.slots.some(slot => slot.id === 'neighbor'), 'Expanded crossing bounds do not claim unseen saved neighbors were edited');
    assert.equal(saved.slots.filter(slot => !slot.id).length, 1); assert.equal(saved.slots.length, 4);
});
test('repeated clears carry server-stamped previous descriptors and never resurrect cleared saved slots', async () => {
    const generated = { ...shift(0, 48, ['a'], 'original'), source: 1 }, firstKept = { ...shift(0, 12, ['a'], 'original'), source: 1 };
    const options = { now: start, slots: [generated], clearSlots: [firstKept] }; const ui = setup(options); await ui.flush();
    await ui.input(ui.get('clear-start'), '2026-10-12T12:00'); await ui.click('clear-range');
    options.clearSlots = []; await ui.input(ui.get('clear-start'), '2026-10-12T00:00'); await ui.click('clear-range');
    const second = ui.request('/preview?clear=true')[1].body;
    assert.equal(second.clearRanges.length, 2); assert(second.clearRanges[0].clearedAtUtc); assert.equal(second.clearRanges[1].clearedAtUtc, undefined);
    assert.equal(second.slots.length, 1); assert.equal(second.slots[0].endsAtUtc, firstKept.endsAtUtc); assert.equal(second.slots[0].source, 1);
    await ui.click('undo-schedule'); await ui.preview(); assert.equal(ui.request('/preview').at(-1).body.clearRanges.length, 1);
    await ui.click('redo-schedule'); await ui.click('save-schedule');
    const saved = ui.request('/save')[0].body; assert.equal(saved.clearRanges.length, 2); assert.deepEqual(saved.slots, []);
});
test('broad clear ignores unsaved GET suggestions and invalid or skipped local ranges do not request a preview', async () => {
    const ui = setup({ now: start, saved: false, savedSlots: [shift(180, 200, ['b'], 'suggestion')] }); await ui.flush();
    await ui.input(ui.get('clear-end'), '2026-10-27T00:00'); await ui.click('clear-range');
    assert(!ui.request('/preview?clear=true')[0].body.slots.some(slot => slot.id === 'suggestion'));
    await ui.input(ui.get('clear-start'), '2026-10-28T00:00'); await ui.click('clear-range'); assert.equal(ui.request('/preview?clear=true').length, 1);
    assert.match(ui.get('schedule-errors').textContent, /End must be after start/);
    const dst = setup({ zone: 'America/New_York' }); await dst.flush();
    await dst.input(dst.get('clear-start'), '2026-03-08T02:30'); await dst.input(dst.get('clear-end'), '2026-03-09T03:00'); await dst.click('clear-range');
    assert.equal(dst.request('/preview?clear=true').length, 0); assert.match(dst.get('schedule-errors').textContent, /Invalid team local time/);
});
test('manual edits to generated shifts clear rotation metadata before clear and undo restores it', async () => {
    const ui = setup({ now: start, slots: [{ ...shift(0, 24, ['a'], 'generated'), source: 1, rotationPosition: 7 }] }); await ui.flush();
    await ui.input(tableInput(ui, 0, 'end'), '2026-10-13T00:17'); await ui.preview();
    assert.equal(ui.request('/preview').at(-1).body.slots[0].source, 0);
    assert.equal(ui.request('/preview').at(-1).body.slots[0].rotationPosition, null);
    await ui.click('undo-schedule'); await ui.preview(); assert.equal(ui.request('/preview').at(-1).body.slots[0].source, 1);
    assert.equal(ui.request('/preview').at(-1).body.slots[0].rotationPosition, 7);
    await ui.check(ui.get('shift-rows').children[0].children[2], 'b', true); await ui.click('clear-range');
    assert.equal(ui.request('/preview?clear=true')[0].body.slots[0].source, 0);
    assert.equal(ui.request('/preview?clear=true')[0].body.slots[0].rotationPosition, null);
});
test('new generated previews are clearable but manual draft edits protect them from generated-only clearing', async () => {
    const generated = { ...shift(12, 20, ['b']), source: 1, rotationPosition: 9 };
    const ui = setup({ now: start, slots: [], generatedSlots: [generated], clearSlots: slots => slots.filter(slot => slot.source !== 1) }); await ui.flush();
    await ui.input(ui.get('range-start'), '2026-10-12T00:00'); await ui.input(ui.get('range-end'), '2026-10-19T00:00');
    await ui.click('generate'); await ui.click('replace-apply');
    await ui.click('clear-range');
    const first = ui.request('/preview?clear=true')[0].body;
    assert.equal(first.clearRanges[0].includeManual, false); assert.equal(first.slots[0].source, 1); assert.equal(first.slots[0].rotationPosition, 9);
    assert.deepEqual(ui.draft(), []); assert.equal(ui.request('/save').length, 0);
    await ui.click('undo-schedule');
    await ui.click(ui.bar(0, 'b')); ui.get('dialog-end').value = '2026-10-12T20:17'; await ui.click('dialog-apply');
    await ui.click('clear-range');
    const second = ui.request('/preview?clear=true')[1].body;
    assert.equal(second.slots[0].source, 0); assert.equal(second.slots[0].rotationPosition, null);
    assert.equal(ui.draft()[0].endLocal, '2026-10-12T20:17'); assert(ui.bar(0, 'b'));
});
test('manual gestures and popup assignment edits clear generated rotation metadata while no-op edits preserve it', async () => {
    const generated = { ...shift(12, 20, ['a']), source: 1, rotationPosition: 4 };
    const ui = setup({ now: start, slots: [generated] }); await ui.flush();
    const verify = async (source, rotationPosition) => {
        await ui.preview(); const slot = ui.request('/preview').at(-1).body.slots[0];
        assert.equal(slot.source, source); assert.equal(slot.rotationPosition, rotationPosition);
    };
    await ui.input(tableInput(ui, 0, 'end'), '2026-10-12T20:00');
    await ui.click(ui.bar()); await ui.click('dialog-apply'); await verify(1, 4);
    await ui.drag(ui.bar(), 14, 14, ui.lane('b')); await verify(0, null);
    await ui.click('undo-schedule'); await verify(1, 4);
    await ui.drag(ui.bar().querySelector('[data-resize="end"]'), 20, 21); await verify(0, null);
    await ui.click('undo-schedule');
    await ui.fire(ui.bar(), 'keydown', { key: 'ArrowRight' }); await verify(0, null);
    await ui.click('undo-schedule');
    await ui.click(ui.bar()); await ui.check(ui.get('dialog-members'), 'b', true); await ui.click('dialog-apply'); await verify(0, null);
    await ui.click('undo-schedule'); await verify(1, 4);
    await ui.click('copy-range'); await ui.preview();
    const copied = ui.request('/preview').at(-1).body.slots;
    assert.equal(copied[0].source, 1); assert.equal(copied[0].rotationPosition, 4);
    assert(copied.slice(1).every(slot => slot.source === 0 && slot.rotationPosition == null));
});
