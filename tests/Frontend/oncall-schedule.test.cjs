const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const test = require('node:test');

function setup({ nextSaved = false, departed = false, saveFails = false } = {}) {
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
    const date = new Date('2026-10-12T00:00:00Z');
    const source = Array.from({ length: 63 }, (_, index) => {
        const time = new Date(date.getTime() + (index - 21) * 8 * 3600000);
        const end = new Date(time.getTime() + 8 * 3600000);
        return { startsAtUtc: time.toISOString(), localStart: time.toISOString().replace('T', ' '),
            localEnd: end.toISOString().replace('T', ' '), userIds: [departed && index === 21 ? 'departed' : `user${index % 3}`] };
    });
    const weeks = [-1, 0, 1].map((offset, index) => ({ startUtc: new Date(date.getTime() + offset * 7 * 86400000).toISOString(),
        saved: index !== 2 || nextSaved, restHours: 16, minimumStaff: 1, available: true }));
    if (!nextSaved) source.slice(42).forEach(slot => slot.userIds = []);
    root.dataset = { slots: JSON.stringify(source), weeks: JSON.stringify(weeks), start: date.toISOString(),
        groupId: 'group', version: 'version', zone: 'UTC', saveUrl: '/save', generateUrl: '/generate' };
    add(root, 'input', null, { attributes: { name: '__RequestVerificationToken' }, value: 'token' });
    for (const id of ['oncall-schedule-errors', 'oncall-drag-status', 'oncall-edit-count', 'oncall-draft-errors',
        'oncall-draft-validation', 'oncall-draft-summary', 'oncall-mode-help']) add(root, 'div', id);
    for (const id of ['oncall-undo-schedule', 'oncall-reset-schedule', 'oncall-save-schedule', 'oncall-copy-next', 'oncall-generate'])
        add(root, 'button', id);
    add(root, 'select', 'oncall-edit-slot', { value: '21' });
    for (const mode of ['move', 'add', 'remove']) add(root, 'button', null, { dataset: { scheduleMode: mode } });
    const chart = add(body, 'div', 'oncall-gantt');
    const gantt = add(chart, 'div', null, { className: 'ac-gantt' });
    const rowMap = {};
    for (const id of ['user0', 'user1', 'user2', ...(departed ? ['departed'] : [])]) {
        add(root, 'input', null, { className: 'oncall-edit-member', value: id, dataset: { departed: String(id === 'departed') } });
        const row = add(gantt, 'div', null, { dataset: { userId: id, name: id, color: '0', departed: String(id === 'departed') } });
        rowMap[id] = row;
        add(row, 'div', null, { className: 'ac-gantt-name' });
        add(row, 'span', null, { className: 'ac-gantt-hours' });
        add(row, 'button', null, { className: 'ac-gantt-week' });
        const lane = add(row, 'div', null, { className: 'ac-gantt-lane' });
        for (let index = 18; index < 45; index++) {
            const edge = index < 21 ? 'previous' : index >= 42 ? 'next' : undefined;
            add(lane, 'button', null, { className: 'ac-gantt-cell', hidden: Boolean(edge),
                dataset: { slot: String(index), edge: edge || '' },
                style: { left: `${(index - 21) * 100 / 21}%`, width: `${100 / 21}%` } });
        }
    }
    for (const edge of ['previous', 'next']) {
        const toggle = add(body, 'button', `toggle-${edge}`, { dataset: { oncallEdge: edge } });
        toggle.setAttribute('aria-expanded', 'false');
    }
    for (const stat of ['shifts', 'staff', 'hours']) add(body, 'span', `stat-${stat}`, { dataset: { oncallStat: stat } });
    for (const id of ['oncall-save-state', 'oncall-shift-summary', 'oncall-shift-start', 'oncall-shift-end',
        'oncall-copy-confirm']) add(body, 'div', id);
    add(body, 'button', 'oncall-copy-apply');
    let reloads = 0;
    const requests = [];
    const modal = { shown: false, show() { this.shown = true; }, hide() { this.shown = false; } };
    const window = { bootstrap: { Modal: { getOrCreateInstance: () => modal } },
        location: { reload() { reloads++; } }, listeners: {},
        addEventListener(type, handler) { this.listeners[type] = handler; } };
    const document = { body, getElementById: id => elements[id], querySelectorAll: selector => body.querySelectorAll(selector),
        querySelector: selector => body.querySelector(selector), createElement: tag => new Element(tag),
        createTextNode: text => new Element('text', { textContent: text }), addEventListener() {} };
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
    return { elements, chart, root, requests, source, modal, window, assign, rowMap, reloads: () => reloads,
        click: id => elements[id].click(),
        cell: (id, slot) => rowMap[id].querySelector(`.ac-gantt-cell[data-slot="${slot}"]`),
        shift: (id, slot) => rowMap[id].querySelector(`.ac-gantt-shift[data-slot="${slot}"]`) };
}

test('24h edges start collapsed, expand independently and retain stable positions after redraw', async () => {
    const ui = setup({ nextSaved: true });
    assert(ui.cell('user0', 18).hidden);
    assert(ui.cell('user0', 42).hidden);
    assert.equal(Number.parseFloat(ui.cell('user0', 21).style.left), 0);
    assert(!ui.cell('user0', 21).hidden);
    await ui.click('toggle-previous');
    assert(!ui.cell('user0', 18).hidden);
    assert(ui.cell('user0', 42).hidden);
    assert.equal(Number.parseFloat(ui.cell('user0', 21).style.left), 12.5);
    await ui.click('toggle-next');
    const left = Number.parseFloat(ui.cell('user0', 21).style.left);
    assert.equal(left, 100 / 9);
    await ui.assign(42, 'user1', true);
    assert.equal(Number.parseFloat(ui.cell('user0', 21).style.left), left);
    assert.equal(Number.parseFloat(ui.shift('user1', 42).style.left), 800 / 9);
    await ui.click('toggle-previous');
    await ui.click('toggle-next');
    assert.equal(Number.parseFloat(ui.cell('user0', 21).style.left), 0);
    assert(ui.shift('user1', 42).hidden);
    assert.match(ui.elements['oncall-edit-count'].textContent, /含下一周/);
    assert.equal(ui.elements['stat-shifts'].textContent, 21);
});

test('copy uses current draft, fills the complete next week and is undoable without saving', async () => {
    const ui = setup();
    await ui.assign(21, 'user1', true);
    await ui.click('oncall-copy-next');
    assert.equal(ui.requests.length, 0);
    assert(!ui.modal.shown);
    assert.equal(ui.elements['toggle-next'].getAttribute('aria-expanded'), 'true');
    assert(ui.shift('user1', 42));
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
    assert(!ui.shift('user0', 42));
    assert(ui.shift('user1', 21));
    await ui.click('oncall-copy-next');
    await ui.click('oncall-reset-schedule');
    assert(!ui.shift('user0', 42));
    assert(!ui.shift('user1', 21));
    assert.equal(ui.elements['oncall-edit-count'].textContent, '已修改 0 个班次');
    await ui.click('oncall-undo-schedule');
    assert(ui.shift('user1', 42));
});

test('saved or modified next week requires confirmation; cancel leaves drafts unchanged', async () => {
    for (const nextSaved of [true, false]) {
        const ui = setup({ nextSaved });
        await ui.assign(42, 'user1', true);
        await ui.click('oncall-copy-next');
        assert(ui.modal.shown);
        assert(ui.shift('user1', 42));
        assert.equal(ui.requests.length, 0);
        ui.modal.hide();
        assert(ui.shift('user1', 42));
        await ui.click('oncall-copy-next');
        await ui.click('oncall-copy-apply');
        assert(!ui.modal.shown);
        assert(ui.shift('user0', 42));
        assert(!ui.shift('user1', 42));
        await ui.click('oncall-undo-schedule');
        assert(ui.shift('user1', 42));
    }
});

test('departed engineers cannot be copied to new assignments', async () => {
    const ui = setup({ departed: true });
    await ui.click('oncall-copy-next');
    assert.equal(ui.elements['oncall-schedule-errors'].children.length, 1);
    assert.match(ui.elements['oncall-schedule-errors'].children[0].textContent, /已退出/);
    assert(!ui.shift('departed', 42));
    assert.equal(ui.elements['oncall-edit-count'].textContent, '已修改 0 个班次');
});

test('previous and next week form edits submit together and validation failure retains drafts', async () => {
    const ui = setup({ nextSaved: true, saveFails: true });
    await ui.assign(20, 'user0', true);
    await ui.assign(42, 'user1', true);
    await ui.click('oncall-save-schedule');
    assert.equal(ui.reloads(), 0);
    assert.deepEqual(ui.requests[0].adjacentWeeks.map(week => week.slots.length), [21, 21]);
    assert(ui.requests[0].adjacentWeeks[0].slots[20].userIds.includes('user0'));
    assert(ui.requests[0].adjacentWeeks[1].slots[0].userIds.includes('user1'));
    assert(ui.shift('user0', 20));
    assert(ui.shift('user1', 42));
    assert.match(ui.elements['oncall-schedule-errors'].children[0].textContent, /未保存/);
    assert.equal(ui.elements['oncall-undo-schedule'].disabled, false);
    assert.equal(ui.elements['oncall-generate'].disabled, true);
    let prevented = false;
    ui.window.listeners.beforeunload({ preventDefault() { prevented = true; } });
    assert(prevented);
});

test('click move crosses the week boundary; whole-week swaps still affect only the main week', async () => {
    const ui = setup({ nextSaved: true });
    await ui.click('toggle-next');
    await ui.shift('user2', 41).click();
    await ui.cell('user1', 42).click();
    assert(!ui.shift('user2', 41));
    assert(ui.shift('user1', 42));
    await ui.click('oncall-undo-schedule');
    await ui.rowMap.user0.querySelector('.ac-gantt-week').click();
    await ui.rowMap.user1.querySelector('.ac-gantt-week').click();
    assert(ui.shift('user1', 21));
    assert(ui.shift('user0', 42));
    assert(!ui.shift('user1', 42));
    await ui.click('oncall-save-schedule');
    assert.equal(ui.requests[0].adjacentWeeks.length, 0);
});
