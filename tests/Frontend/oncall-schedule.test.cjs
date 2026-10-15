const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const test = require('node:test');

function setup({ nextSaved = false, departed = false, saveFails = false, externalShifts = false,
    previousAvailable = true, startUtc = '2026-10-12T00:00:00Z', zone = 'UTC' } = {}) {
    class Event {
        constructor(type, options = {}) { this.type = type; Object.assign(this, options); }
    }
    class Element {
        constructor(tag = 'div', attributes = {}) {
            this.tagName = tag;
            this.dataset = {};
            this.style = {};
            this.children = [];
            this.listeners = {};
            this.attributes = {};
            this.className = '';
            this.textContent = '';
            this.hidden = false;
            Object.assign(this, attributes);
            this.classList = {
                contains: name => this.className.split(' ').includes(name),
                add: name => { if (!this.classList.contains(name)) this.className += ` ${name}`; },
                remove: name => { this.className = this.className.split(' ').filter(item => item !== name).join(' '); },
                toggle: (name, active) => active ? this.classList.add(name) : this.classList.remove(name)
            };
        }
        append(...items) { items.forEach(item => { item.parent = this; this.children.push(item); }); }
        remove() { this.parent.children = this.parent.children.filter(item => item !== this); }
        replaceChildren(...items) { this.children = []; this.append(...items); }
        contains(item) { return item === this || this.children.some(child => child.contains(item)); }
        setAttribute(name, value) { this.attributes[name] = String(value); }
        getAttribute(name) { return this.attributes[name]; }
        matches(selector) {
            return selector.split(',').some(part => {
                const excluded = [...part.matchAll(/:not\(([^)]+)\)/g)].map(match => match[1]);
                if (excluded.some(selector => this.matches(selector))) return false;
                part = part.replace(/:not\([^)]+\)/g, '');
                const tag = part.trim().match(/^[a-z]+/);
                const classes = [...part.matchAll(/\.([\w-]+)/g)];
                const attributes = [...part.matchAll(/\[([\w-]+)(?:="([^"]*)")?\]/g)];
                return (!tag || this.tagName === tag[0]) && classes.every(match => this.classList.contains(match[1])) &&
                    attributes.every(([, name, value]) => {
                        if (name === 'style') return Object.keys(this.style).length > 0;
                        const key = name.slice(5).replace(/-([a-z])/g, (_, letter) => letter.toUpperCase());
                        const actual = name.startsWith('data-') ? this.dataset[key] : this.attributes[name];
                        return actual !== undefined && (value === undefined || String(actual) === value);
                    });
            });
        }
        closest(selector) { return this.matches(selector) ? this : this.parent?.closest(selector); }
        querySelectorAll(selector) {
            return this.children.flatMap(child => [...(child.matches(selector) ? [child] : []), ...child.querySelectorAll(selector)]);
        }
        querySelector(selector) { return this.querySelectorAll(selector)[0]; }
        addEventListener(type, handler) { (this.listeners[type] ??= []).push(handler); }
        dispatchEvent(event) {
            event.target ??= this;
            const results = (this.listeners[event.type] || []).map(handler => handler(event));
            if (event.bubbles && this.parent) results.push(this.parent.dispatchEvent(event));
            return Promise.all(results);
        }
        click() { return this.dispatchEvent(new Event('click', { bubbles: true, detail: 0 })); }
        scrollIntoView() {}
        setPointerCapture(id) { this.capturedPointer = id; }
        hasPointerCapture(id) { return this.capturedPointer === id; }
        releasePointerCapture() { this.capturedPointer = undefined; }
        getBoundingClientRect() { return { left: 0, right: this.classList.contains('ac-gantt-name') ? 160 : 1000 }; }
    }
    const elements = {};
    const body = new Element('body');
    function add(parent, tag, id, attributes) {
        const item = new Element(tag, attributes);
        if (id) elements[id] = item;
        parent.append(item);
        return item;
    }
    const root = add(body, 'div', 'oncall-schedule');
    const date = new Date(startUtc);
    const localTime = value => {
        const parts = new Intl.DateTimeFormat('sv-SE', { timeZone: zone, dateStyle: 'short', timeStyle: 'short' }).format(value);
        const offset = new Intl.DateTimeFormat('en', { timeZone: zone, timeZoneName: 'longOffset' }).formatToParts(value)
            .find(part => part.type === 'timeZoneName').value.replace('GMT', '') || '+00:00';
        return `${parts} ${offset}`;
    };
    const source = Array.from({ length: 63 }, (_, index) => {
        const time = new Date(date.getTime() + (index - 21) * 8 * 3600000);
        const end = new Date(time.getTime() + 8 * 3600000);
        return { startsAtUtc: time.toISOString(), localStart: localTime(time),
            localEnd: localTime(end), userIds: [departed && index === 21 ? 'departed' : `user${index % 3}`] };
    });
    const weeks = [-1, 0, 1].map((offset, index) => ({ startUtc: new Date(date.getTime() + offset * 7 * 86400000).toISOString(),
        saved: index !== 2 || nextSaved, restHours: 16, minimumStaff: 1, available: index !== 0 || previousAvailable }));
    if (!nextSaved) source.slice(42).forEach(slot => slot.userIds = []);
    root.dataset = { slots: JSON.stringify(source), weeks: JSON.stringify(weeks), start: date.toISOString(),
        groupId: 'group', version: 'version', zone, saveUrl: '/save', generateUrl: '/generate' };
    add(root, 'input', null, { attributes: { name: '__RequestVerificationToken' }, value: 'token' });
    for (const id of ['oncall-schedule-errors', 'oncall-drag-status', 'oncall-edit-count', 'oncall-draft-errors',
        'oncall-draft-validation', 'oncall-draft-summary', 'oncall-mode-help']) add(root, 'div', id);
    for (const id of ['oncall-undo-schedule', 'oncall-reset-schedule', 'oncall-save-schedule', 'oncall-copy-next', 'oncall-generate'])
        add(root, 'button', id);
    const select = add(root, 'select', 'oncall-edit-slot', { value: '21' });
    for (let index = 21; index < 42; index++) add(select, 'option', null, { value: String(index) });
    for (const mode of ['move', 'add', 'remove']) add(root, 'button', null, { dataset: { scheduleMode: mode } });
    const frame = add(body, 'div', null, { className: 'ac-gantt-frame' });
    const chart = add(frame, 'div', 'oncall-gantt', { scrollLeft: 400, scrollWidth: 2400, clientWidth: 800 });
    const gantt = add(chart, 'div', null, { className: 'ac-gantt' });
    for (const [edge, offset] of [['previous', -36], ['next', 168]]) {
        add(gantt, 'div', null, { className: 'ac-gantt-date ac-gantt-adjacent', hidden: true, dataset: { edge },
            style: { left: `${offset / 168 * 100}%`, width: `${36 / 168 * 100}%` } });
    }
    const rowMap = {};
    for (const id of ['user0', 'user1', 'user2', ...(departed ? ['departed'] : [])]) {
        add(root, 'input', null, { className: 'oncall-edit-member', value: id, dataset: { departed: String(id === 'departed') } });
        const row = add(gantt, 'div', null, { dataset: { userId: id, name: id, color: '0', departed: String(id === 'departed') } });
        rowMap[id] = row;
        add(row, 'div', null, { className: 'ac-gantt-name' });
        add(row, 'span', null, { className: 'ac-gantt-hours' });
        add(row, 'button', null, { className: 'ac-gantt-week' });
        const lane = add(row, 'div', null, { className: 'ac-gantt-lane' });
        if (id !== 'departed') {
            for (let index = 21; index < 42; index++) {
                add(lane, 'button', null, { className: 'ac-gantt-cell', dataset: { slot: String(index) },
                    style: { left: `${(index - 21) * 100 / 21}%`, width: `${100 / 21}%` } });
            }
        }
    }
    const external = [];
    if (externalShifts) {
        for (const [id, offset, width, edge] of [['user0', 1.5, 3.75, ''], ['user1', -36, 36, 'previous'], ['user2', 168, 36, 'next']]) {
            const lane = add(rowMap[id], 'div', null, { className: 'ac-gantt-lane ac-gantt-external-lane', dataset: { readonly: 'true' } });
            const shift = add(lane, 'button', null, {
                className: 'ac-gantt-shift ac-gantt-external', hidden: Boolean(edge),
                dataset: { readonly: 'true', groupId: 'other-group', edge, summary: `${id} · Other team · 只读`,
                    start: '2026-10-12 01:30 +00:00', end: '2026-10-12 05:15 +00:00' },
                style: { left: `${offset / 168 * 100}%`, width: `${width / 168 * 100}%` }
            });
            add(shift, 'span', null, { className: 'ac-gantt-time', textContent: 'Other team' });
            external.push(shift);
        }
    }
    for (const edge of ['previous', 'next']) {
        const toggle = add(frame, 'button', `toggle-${edge}`, { className: 'ac-gantt-edge-toggle', dataset: { oncallEdge: edge } });
        toggle.setAttribute('aria-expanded', 'false');
    }
    for (const stat of ['shifts', 'staff', 'hours']) add(body, 'span', `stat-${stat}`, { dataset: { oncallStat: stat } });
    for (const id of ['oncall-save-state', 'oncall-shift-summary', 'oncall-shift-start', 'oncall-shift-end',
        'oncall-copy-confirm']) add(body, 'div', id);
    add(body, 'button', 'oncall-copy-apply');
    let reloads = 0;
    const requests = [];
    const modal = { shown: false, show() { this.shown = true; }, hide() { this.shown = false; } };
    const window = { bootstrap: { Modal: { getOrCreateInstance: () => modal } }, innerWidth: 1000, innerHeight: 800,
        location: { reload() { reloads++; } }, listeners: {},
        addEventListener(type, handler) { this.listeners[type] = handler; } };
    let pointedElement;
    const document = { body, getElementById: id => elements[id], querySelectorAll: selector => body.querySelectorAll(selector),
        querySelector: selector => body.querySelector(selector), createElement: tag => new Element(tag),
        createTextNode: text => new Element('text', { textContent: text }), addEventListener() {},
        elementFromPoint: () => pointedElement };
    const context = vm.createContext({ document, window, Event, structuredClone, Date, Set,
        fetch: async (url, options) => {
            requests.push({ url, ...JSON.parse(options.body) });
            return { ok: !saveFails, json: async () => ({ errors: ['休息不足，未保存'] }) };
        } });
    for (const script of ['oncall-gantt.js', 'oncall-schedule.js'])
        vm.runInContext(fs.readFileSync(path.resolve(__dirname, '../../src/Aiursoft.AlertCenter/wwwroot/scripts', script), 'utf8'), context);
    const member = id => root.querySelectorAll('.oncall-edit-member').find(input => input.value === id);
    async function assign(slot, id, checked) {
        elements['oncall-edit-slot'].value = String(slot);
        member(id).checked = checked;
        await member(id).dispatchEvent(new Event('change'));
    }
    async function drag(from, to) {
        pointedElement = to;
        await from.dispatchEvent(new Event('pointerdown', { bubbles: true, isPrimary: true, button: 0, pointerId: 1, clientX: 200, clientY: 20 }));
        await chart.dispatchEvent(new Event('pointermove', { pointerId: 1, clientX: 300, clientY: 20 }));
        await chart.dispatchEvent(new Event('pointerup', { pointerId: 1, clientX: 300, clientY: 20 }));
    }
    return { elements, chart, frame, root, requests, source, weeks, modal, window, assign, drag, rowMap, external, Event,
        reloads: () => reloads,
        mode: value => root.querySelector(`[data-schedule-mode="${value}"]`).click(),
        click: id => elements[id].click(),
        injectCell: (id, slot) => add(rowMap[id].querySelector('.ac-gantt-lane'), 'button', null,
            { className: 'ac-gantt-cell', dataset: { slot: String(slot) } }),
        cell: (id, slot) => rowMap[id].querySelector(`.ac-gantt-cell[data-slot="${slot}"]`),
        shift: (id, slot) => rowMap[id].querySelector(`.ac-gantt-shift[data-slot="${slot}"]`),
        adjacent: (id, edge) => rowMap[id].querySelectorAll(`.ac-gantt-shift:not(.ac-gantt-external)[data-edge="${edge}"]`) };
}

function near(actual, expected) {
    assert(Math.abs(Number.parseFloat(actual) - expected) < 0.000001, `${actual} should equal ${expected}`);
}

test('36h edges start collapsed even at the anchor, expand independently and scroll toward the opened edge', async () => {
    const ui = setup({ nextSaved: true, previousAvailable: false });
    assert.equal(ui.root.querySelectorAll('option').length, 21);
    assert.equal(ui.root.querySelectorAll('option')[0].value, '21');
    assert.equal(ui.root.querySelectorAll('option')[20].value, '41');
    assert.equal(ui.chart.querySelectorAll('.ac-gantt-cell').length, 63);
    for (const edge of ['previous', 'next']) {
        const toggle = ui.elements[`toggle-${edge}`];
        assert(ui.frame.contains(toggle));
        assert(!ui.chart.contains(toggle));
        assert(toggle.classList.contains('ac-gantt-edge-toggle'));
        assert.equal(toggle.getAttribute('aria-expanded'), 'false');
        assert.equal(toggle.textContent, `展开${edge === 'previous' ? '前' : '后'} 36 小时`);
        assert(ui.chart.querySelectorAll(`[data-edge="${edge}"]`).every(item => item.hidden));
        assert.equal(ui.chart.querySelectorAll(`.ac-gantt-shift[data-edge="${edge}"]`).length, 5);
    }
    assert.equal(ui.cell('user0', 18), undefined);
    assert.equal(ui.cell('user0', 42), undefined);
    near(ui.cell('user0', 21).style.left, 0);
    await ui.click('toggle-previous');
    assert.equal(ui.chart.scrollLeft, 0);
    assert.equal(ui.elements['toggle-previous'].textContent, '折叠前 36 小时');
    assert(!ui.adjacent('user1', 'previous')[0].hidden);
    assert(ui.adjacent('user0', 'next')[0].hidden);
    near(ui.cell('user0', 21).style.left, 36 / 204 * 100);
    await ui.click('toggle-next');
    assert.equal(ui.chart.scrollLeft, ui.chart.scrollWidth - ui.chart.clientWidth);
    assert.equal(ui.elements['toggle-next'].textContent, '折叠后 36 小时');
    near(ui.cell('user0', 21).style.left, 15);
    const originalAdjacent = ui.adjacent('user0', 'next')[0];
    await ui.assign(21, 'user1', true);
    assert(!ui.chart.contains(originalAdjacent));
    assert.equal(ui.chart.querySelectorAll('.ac-gantt-shift[data-edge]').length, 10);
    near(ui.cell('user0', 21).style.left, 15);
    near(ui.adjacent('user0', 'next')[0].style.left, 85);
    await ui.click('toggle-previous');
    assert.equal(ui.elements['toggle-previous'].textContent, '展开前 36 小时');
    assert(ui.adjacent('user1', 'previous')[0].hidden);
    assert(!ui.adjacent('user0', 'next')[0].hidden);
    near(ui.cell('user0', 21).style.left, 0);
    await ui.click('toggle-next');
    assert(ui.adjacent('user0', 'next')[0].hidden);
    assert(!ui.cell('user0', 21).hidden);
    assert.equal(ui.elements['stat-shifts'].textContent, 21);
});

test('own adjacent shifts are clipped to 36 elapsed UTC hours with full details across both DST transitions', async () => {
    for (const startUtc of ['2026-03-08T12:00:00Z', '2026-10-24T12:00:00Z']) {
        const ui = setup({ nextSaved: true, startUtc, zone: 'America/New_York' });
        assert.equal(ui.source.length, 63);
        assert.equal(ui.weeks.length, 3);
        const previous = ui.adjacent('user1', 'previous')[0];
        const next = ui.adjacent('user1', 'next').at(-1);
        near(previous.style.left, -36 / 168 * 100);
        near(previous.style.width, 4 / 168 * 100);
        near(next.style.left, 200 / 168 * 100);
        near(next.style.width, 4 / 168 * 100);
        for (const [shift, index] of [[previous, 16], [next, 46]]) {
            assert.equal(shift.dataset.readonly, 'true');
            assert.equal(shift.dataset.slot, undefined);
            assert.equal(shift.dataset.start, ui.source[index].localStart);
            assert.equal(shift.dataset.end, ui.source[index].localEnd);
            assert.match(shift.dataset.summary, /只读/);
            await shift.children[0].click();
            assert.equal(ui.elements['oncall-shift-start'].textContent, ui.source[index].localStart);
            assert.equal(ui.elements['oncall-shift-end'].textContent, ui.source[index].localEnd);
            assert(!shift.classList.contains('ac-gantt-selected'));
        }
        await ui.click('toggle-previous');
        await ui.click('toggle-next');
        near(previous.style.left, 0);
        near(previous.style.width, 4 / 240 * 100);
        near(next.style.left, 236 / 240 * 100);
        near(next.style.width, 4 / 240 * 100);
        const offsets = ui.source.slice(16, 47).map(slot => slot.localStart.slice(-6));
        assert(offsets.includes('-04:00'));
        assert(offsets.includes('-05:00'));
    }
});

test('copy uses current draft, fills the complete next week and is undoable without saving', async () => {
    const ui = setup();
    await ui.assign(21, 'user1', true);
    await ui.click('oncall-copy-next');
    assert.equal(ui.requests.length, 0);
    assert(!ui.modal.shown);
    assert.equal(ui.elements['toggle-next'].getAttribute('aria-expanded'), 'true');
    assert(ui.adjacent('user1', 'next').some(shift => shift.dataset.start === ui.source[42].localStart));
    assert(ui.adjacent('user1', 'next').every(shift => shift.dataset.readonly === 'true' && shift.dataset.slot === undefined));
    await ui.click('oncall-save-schedule');
    assert.equal(ui.reloads(), 1);
    const payload = ui.requests[0];
    assert.equal(payload.slots.length, 21);
    assert.equal(payload.adjacentWeeks.length, 1);
    assert.equal(payload.adjacentWeeks[0].slots.length, 21);
    assert.deepEqual(payload.adjacentWeeks[0].slots.map(slot => slot.userIds), payload.slots.map(slot => slot.userIds));
    assert.equal(Date.parse(payload.adjacentWeeks[0].startUtc) - Date.parse(payload.startUtc), 7 * 86400000);
});

test('copy undo and reset restore all weeks and keep the current draft when undoing copy', async () => {
    const ui = setup();
    await ui.assign(21, 'user1', true);
    await ui.click('oncall-copy-next');
    await ui.click('oncall-undo-schedule');
    assert.equal(ui.adjacent('user0', 'next').length, 0);
    assert(ui.shift('user1', 21));
    await ui.click('oncall-copy-next');
    await ui.click('oncall-reset-schedule');
    assert.equal(ui.adjacent('user0', 'next').length, 0);
    assert(!ui.shift('user1', 21));
    assert.equal(ui.elements['oncall-edit-count'].textContent, '已修改 0 个班次');
    await ui.click('oncall-undo-schedule');
    assert(ui.adjacent('user1', 'next').some(shift => shift.dataset.start === ui.source[42].localStart));
});

test('saved or previously copied next week requires confirmation; cancel leaves drafts unchanged', async () => {
    for (const nextSaved of [true, false]) {
        const ui = setup({ nextSaved });
        if (!nextSaved) await ui.click('oncall-copy-next');
        await ui.assign(21, 'user1', true);
        await ui.click('oncall-copy-next');
        assert(ui.modal.shown);
        assert(ui.adjacent('user0', 'next').some(shift => shift.dataset.start === ui.source[42].localStart));
        assert(!ui.adjacent('user1', 'next').some(shift => shift.dataset.start === ui.source[42].localStart));
        assert.equal(ui.requests.length, 0);
        ui.modal.hide();
        assert(!ui.adjacent('user1', 'next').some(shift => shift.dataset.start === ui.source[42].localStart));
        await ui.click('oncall-copy-next');
        await ui.click('oncall-copy-apply');
        assert(!ui.modal.shown);
        assert(ui.adjacent('user0', 'next').some(shift => shift.dataset.start === ui.source[42].localStart));
        assert(ui.adjacent('user1', 'next').some(shift => shift.dataset.start === ui.source[42].localStart));
        assert.equal(String(ui.elements['oncall-edit-slot'].value), '21');
        await ui.click('oncall-undo-schedule');
        assert(!ui.adjacent('user1', 'next').some(shift => shift.dataset.start === ui.source[42].localStart));
        assert(ui.shift('user1', 21));
    }
});

test('departed engineers cannot be copied to new assignments', async () => {
    const ui = setup({ departed: true });
    await ui.click('oncall-copy-next');
    assert.equal(ui.elements['oncall-schedule-errors'].children.length, 1);
    assert.match(ui.elements['oncall-schedule-errors'].children[0].textContent, /已退出/);
    assert.equal(ui.adjacent('departed', 'next').length, 0);
    assert.equal(ui.elements['oncall-edit-count'].textContent, '已修改 0 个班次');
});

test('form rejects adjacent indices while failed saves preserve current edits and the complete copied next week', async () => {
    const ui = setup({ nextSaved: true, saveFails: true });
    for (const [index, user] of [[20, 'user0'], [42, 'user1']]) {
        await ui.assign(index, user, true);
        assert.equal(ui.elements['oncall-edit-count'].textContent, '已修改 0 个班次');
        assert.equal(ui.elements['oncall-undo-schedule'].disabled, true);
    }
    await ui.assign(21, 'user1', true);
    await ui.click('oncall-copy-next');
    await ui.click('oncall-copy-apply');
    await ui.assign(46, 'user2', true);
    await ui.click('oncall-save-schedule');
    assert.equal(ui.reloads(), 0);
    assert.equal(ui.requests[0].adjacentWeeks.length, 1);
    assert.equal(ui.requests[0].adjacentWeeks[0].slots.length, 21);
    assert.equal(ui.requests[0].adjacentWeeks[0].startUtc, ui.weeks[2].startUtc);
    assert.deepEqual(ui.requests[0].adjacentWeeks[0].slots.map(slot => slot.userIds), ui.requests[0].slots.map(slot => slot.userIds));
    assert(ui.shift('user1', 21));
    assert(ui.adjacent('user1', 'next').some(shift => shift.dataset.start === ui.source[42].localStart));
    assert(!ui.adjacent('user2', 'next').some(shift => shift.dataset.start === ui.source[46].localStart));
    assert.match(ui.elements['oncall-schedule-errors'].children[0].textContent, /未保存/);
    assert.equal(ui.elements['oncall-undo-schedule'].disabled, false);
    assert.equal(ui.elements['oncall-generate'].disabled, true);
    let prevented = false;
    ui.window.listeners.beforeunload({ preventDefault() { prevented = true; } });
    assert(prevented);
});

test('readonly external bars survive redraw, undo, reset and copy without entering totals or submitted slots', async () => {
    const ui = setup({ externalShifts: true });
    const original = ui.external.map(shift => ({ shift, dataset: { ...shift.dataset }, children: [...shift.children] }));
    function unchanged() {
        assert.equal(ui.chart.querySelectorAll('.ac-gantt-external').length, 3);
        for (const { shift, dataset, children } of original) {
            assert(ui.chart.contains(shift));
            assert.deepEqual(shift.dataset, dataset);
            assert.deepEqual(shift.children, children);
            assert.equal(shift.dataset.slot, undefined);
        }
    }
    unchanged();
    assert.equal(ui.elements['stat-shifts'].textContent, 21);
    assert.equal(ui.elements['stat-staff'].textContent, 3);
    assert.equal(ui.elements['stat-hours'].textContent, '168 小时');
    assert.equal(ui.rowMap.user0.querySelector('.ac-gantt-hours').textContent, '7 班 / 56 小时');
    assert(ui.external[1].parent.hidden);
    assert(ui.external[2].parent.hidden);
    await ui.click('toggle-previous');
    assert(!ui.external[1].hidden);
    assert(!ui.external[1].parent.hidden);
    const expectedLeft = Number.parseFloat(ui.external[0].style.left);
    await ui.assign(21, 'user1', true);
    unchanged();
    assert.equal(Number.parseFloat(ui.external[0].style.left), expectedLeft);
    await ui.click('oncall-undo-schedule');
    unchanged();
    await ui.click('oncall-copy-next');
    unchanged();
    assert(!ui.external[2].hidden);
    assert(!ui.external[2].parent.hidden);
    await ui.click('oncall-reset-schedule');
    unchanged();
    assert.equal(ui.adjacent('user0', 'next').length, 0);
    await ui.click('oncall-copy-next');
    await ui.click('oncall-save-schedule');
    unchanged();
    assert.equal(ui.requests.length, 1);
    assert.deepEqual(ui.requests[0].slots, ui.source.slice(21, 42).map(({ startsAtUtc, userIds }) => ({ startsAtUtc, userIds })));
    assert.deepEqual(ui.requests[0].adjacentWeeks[0].slots.map(slot => slot.userIds), ui.requests[0].slots.map(slot => slot.userIds));
    assert(!JSON.stringify(ui.requests[0]).includes('other-group'));
    assert.equal(ui.elements['stat-hours'].textContent, '168 小时');
    await ui.click('toggle-previous');
    assert(ui.external[1].hidden);
    assert(ui.external[1].parent.hidden);
});

test('readonly external bars and lanes cannot be edited or used as move and whole-week targets', async () => {
    const ui = setup({ nextSaved: true, externalShifts: true });
    const external = ui.external[0];
    const initialCount = ui.elements['oncall-edit-count'].textContent;
    await external.children[0].click();
    assert(!external.classList.contains('ac-gantt-selected'));
    assert.equal(ui.elements['oncall-shift-summary'].textContent, external.dataset.summary);
    assert.equal(ui.elements['oncall-shift-start'].textContent, external.dataset.start);
    assert.equal(ui.elements['oncall-shift-end'].textContent, external.dataset.end);
    for (const mode of ['add', 'remove']) {
        await ui.mode(mode);
        await external.children[0].click();
        await external.parent.click();
        assert.equal(ui.elements['oncall-edit-count'].textContent, initialCount);
        assert(ui.chart.contains(external));
        assert.equal(ui.elements['oncall-undo-schedule'].disabled, true);
    }
    await ui.mode('move');
    await external.dispatchEvent(new ui.Event('pointerdown', { bubbles: true, isPrimary: true, button: 0, pointerId: 1 }));
    assert(!external.classList.contains('ac-gantt-selected'));
    await ui.drag(external, ui.cell('user0', 22));
    await ui.drag(ui.shift('user1', 22), external);
    await ui.drag(ui.rowMap.user1.querySelector('.ac-gantt-week'), external.parent);
    assert.equal(ui.elements['oncall-edit-count'].textContent, initialCount);
    assert(ui.shift('user1', 22));
    assert(!external.classList.contains('ac-gantt-drop-target'));
    await ui.shift('user1', 22).click();
    await external.click();
    assert.equal(ui.elements['oncall-edit-count'].textContent, initialCount);
    assert(ui.shift('user1', 22).classList.contains('ac-gantt-selected'));
    await ui.cell('user0', 22).click();
    assert(!ui.shift('user1', 22));
    assert(ui.shift('user0', 22));
    assert(ui.chart.contains(external));
    await ui.click('oncall-undo-schedule');
    await ui.rowMap.user1.querySelector('.ac-gantt-week').click();
    await external.parent.click();
    await external.click();
    assert.equal(ui.elements['oncall-edit-count'].textContent, initialCount);
    await ui.rowMap.user0.querySelector('.ac-gantt-week').click();
    assert(ui.shift('user1', 21));
    assert(ui.chart.contains(external));
    assert.equal(external.dataset.slot, undefined);
    await ui.click('oncall-save-schedule');
    assert.equal(ui.requests[0].adjacentWeeks.length, 0);
    assert.equal(ui.requests[0].slots.flatMap(slot => slot.userIds).length, 21);
    assert(!JSON.stringify(ui.requests[0]).includes('other-group'));
});

test('own adjacent bars reject selection, add, remove, moves and whole-week swaps while the current week stays editable', async () => {
    const ui = setup({ nextSaved: true });
    await ui.click('toggle-previous');
    await ui.click('toggle-next');
    const initialCount = ui.elements['oncall-edit-count'].textContent;
    const adjacentAssignments = () => ui.chart.querySelectorAll('.ac-gantt-shift[data-edge]')
        .map(({ dataset: { edge, readonly, start, end, layoutLeft, layoutWidth } }) => ({ edge, readonly, start, end, layoutLeft, layoutWidth }));
    const originalAdjacent = adjacentAssignments();
    for (const edge of ['previous', 'next']) {
        const adjacent = ui.adjacent('user1', edge)[0];
        assert.equal(adjacent.dataset.slot, undefined);
        assert.equal(adjacent.dataset.readonly, 'true');
        assert(!adjacent.className.includes('ac-oncall-color'));
        await adjacent.children[0].click();
        assert(!adjacent.classList.contains('ac-gantt-selected'));
        assert.equal(ui.elements['oncall-shift-start'].textContent, adjacent.dataset.start);
        for (const mode of ['add', 'remove']) {
            await ui.mode(mode);
            await adjacent.children[0].click();
            await adjacent.parent.click();
            assert.equal(ui.elements['oncall-edit-count'].textContent, initialCount);
        }
        await ui.mode('move');
        await ui.drag(adjacent, ui.cell('user0', 22));
        assert.equal(ui.elements['oncall-edit-count'].textContent, initialCount);
        await ui.shift('user2', 41).click();
        await adjacent.click();
        assert(ui.shift('user2', 41).classList.contains('ac-gantt-selected'));
        assert.equal(String(ui.elements['oncall-edit-slot'].value), '41');
        await ui.mode('move');
        await ui.drag(ui.shift('user2', 41), adjacent);
        assert.equal(ui.elements['oncall-edit-count'].textContent, initialCount);
        assert(!adjacent.classList.contains('ac-gantt-drop-target'));
        await ui.rowMap.user0.querySelector('.ac-gantt-week').click();
        await adjacent.click();
        await adjacent.parent.click();
        assert.equal(ui.elements['oncall-edit-count'].textContent, initialCount);
        await ui.mode('move');
        await ui.drag(ui.rowMap.user0.querySelector('.ac-gantt-week'), adjacent);
        assert.equal(ui.elements['oncall-edit-count'].textContent, initialCount);
    }
    assert.equal(ui.elements['oncall-undo-schedule'].disabled, true);
    await ui.shift('user2', 41).click();
    await ui.cell('user1', 41).click();
    assert(!ui.shift('user2', 41));
    assert(ui.shift('user1', 41));
    await ui.click('oncall-undo-schedule');
    await ui.rowMap.user0.querySelector('.ac-gantt-week').click();
    await ui.rowMap.user1.querySelector('.ac-gantt-week').click();
    assert(ui.shift('user1', 21));
    assert.deepEqual(adjacentAssignments(), originalAdjacent);
    await ui.click('oncall-save-schedule');
    assert.equal(ui.requests[0].adjacentWeeks.length, 0);
});

test('chart guards reject noncurrent slot indices even if a stray editable-looking cell exists', async () => {
    const ui = setup({ nextSaved: true });
    for (const index of [16, 20, 42, 46]) {
        const cell = ui.injectCell('user1', index);
        await ui.mode('add');
        await cell.click();
        assert.equal(ui.elements['oncall-edit-count'].textContent, '已修改 0 个班次');
        await ui.mode('move');
        await ui.shift('user0', 21).click();
        await cell.click();
        assert(ui.shift('user0', 21).classList.contains('ac-gantt-selected'));
        assert.equal(ui.elements['oncall-edit-count'].textContent, '已修改 0 个班次');
        await ui.mode('move');
        await ui.drag(ui.shift('user0', 21), cell);
        assert.equal(ui.elements['oncall-edit-count'].textContent, '已修改 0 个班次');
        cell.remove();
    }
    assert.equal(ui.elements['oncall-undo-schedule'].disabled, true);
    await ui.click('oncall-save-schedule');
    assert.equal(ui.requests[0].adjacentWeeks.length, 0);
    assert.deepEqual(ui.requests[0].slots, ui.source.slice(21, 42).map(({ startsAtUtc, userIds }) => ({ startsAtUtc, userIds })));
});
