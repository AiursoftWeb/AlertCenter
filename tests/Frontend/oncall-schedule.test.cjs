const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const test = require('node:test');
const { localTime, copyRange, hoursFor } = require('../../src/Aiursoft.AlertCenter/wwwroot/scripts/oncall-schedule.js');
const source = fs.readFileSync(require.resolve('../../src/Aiursoft.AlertCenter/wwwroot/scripts/oncall-schedule.js'), 'utf8');
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
    assert.deepEqual(copies[0], { startLocal: '2026-10-25T00:00', endLocal: '2026-10-29T22:00', userIds: ['a', 'b'] });
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

function setup({ replace = false, fail = false, messages = {} } = {}) {
    class Element {
        constructor(tag = 'div') { this.tag = tag; this.children = []; this.dataset = {}; this.style = {}; this.listeners = {}; this.value = ''; }
        append(...items) { for (const item of items) { item.parent = this; this.children.push(item); } }
        replaceChildren(...items) { this.children = []; this.append(...items); }
        remove() { this.parent.children = this.parent.children.filter(item => item !== this); }
        setAttribute(key, value) { (this.attributes ??= {})[key] = value; }
        addEventListener(type, handler) { (this.listeners[type] ??= []).push(handler); }
        async dispatchEvent(event) { event.target ??= this; await Promise.all((this.listeners[event.type] || []).map(fn => fn(event))); }
        click() { return this.dispatchEvent({ type: 'click' }); }
        querySelectorAll(selector) {
            return this.children.flatMap(child => [...(child.matches(selector) ? [child] : []), ...child.querySelectorAll(selector)]);
        }
        matches(selector) { return selector === '[data-user-id]' ? !!this.dataset.userId : selector === '[data-draft-shift]' ? !!this.dataset.draftShift : selector.startsWith('.') ? (this.className || '').split(' ').includes(selector.slice(1)) : false; }
        querySelector(selector) { return this.querySelectorAll(selector)[0]; }
    }
    const elements = {};
    for (const id of ['oncall-schedule', 'oncall-shift-rows', 'oncall-draft-notice', 'oncall-save-schedule', 'oncall-reset-schedule', 'oncall-add-shift', 'oncall-copy-range', 'oncall-generate', 'oncall-range-start', 'oncall-range-end', 'oncall-replace-confirm', 'oncall-replace-apply', 'oncall-replace-cancel', 'oncall-replaced-shifts', 'oncall-schedule-errors', 'oncall-schedule-warnings', 'oncall-gantt', 'oncall-warning-lane']) elements[id] = new Element();
    Object.assign(elements['oncall-schedule'].dataset, { groupId: 'group', version: 'version', zone: 'Asia/Shanghai', start, end, slots: JSON.stringify(initial), members: JSON.stringify(members), previewUrl: '/preview', saveUrl: '/save', generateUrl: '/generate' });
    elements['oncall-schedule'].querySelector = () => ({ value: 'csrf' });
    for (const member of members) {
        const row = new Element(); row.dataset = { userId: member.id, name: member.name, color: '0' };
        const hours = new Element(), lane = new Element(); hours.className = 'ac-gantt-hours'; lane.className = 'ac-gantt-lane';
        row.append(hours, lane); elements['oncall-gantt'].append(row);
    }
    const external = new Element(); external.className = 'ac-gantt-external';
    elements['oncall-gantt'].children[0].children[1].append(external);
    const requests = [], window = { listeners: {}, location: { reload() { window.reloads++; } }, reloads: 0, addEventListener(type, fn) { this.listeners[type] = fn; } };
    let queued;
    vm.runInNewContext(source, { document: { getElementById: id => elements[id], createElement: tag => new Element(tag), createTextNode: value => { const el = new Element('text'); el.textContent = value; return el; } }, window, Intl, Date, structuredClone, Event: class { constructor(type) { this.type = type; } }, getLocalizedText: (key, fallback) => messages[key] ?? fallback,
        clearTimeout() { queued = null; }, setTimeout(fn) { queued = fn; }, fetch: async (url, options) => {
            const body = JSON.parse(options.body); requests.push({ url, body, options });
            const slots = body.slots.map(slot => ({ ...slot, startsAtUtc: slot.startsAtUtc || start, endsAtUtc: slot.endsAtUtc || end }));
            return { ok: !(fail && url === '/save'), json: async () => ({ version: 'new-version', slots, warnings: ['Coverage gap'], timelineWarnings: [{ startsAtUtc: start, endsAtUtc: end, message: 'Coverage gap' }], replacedSlots: replace ? initial : [], errors: ['Version conflict'] }) };
        } });
    const flush = () => new Promise(resolve => setImmediate(resolve));
    return { elements, requests, window, external, flush, preview: async () => { if (queued) await queued(); await flush(); } };
}
test('table edits arbitrary boundaries and multiple members; live warnings do not prevent saving', async () => {
    const ui = setup(); await ui.flush();
    const row = ui.elements['oncall-shift-rows'].children[0];
    assert.equal(row.children[0].children[0].value, '2026-10-12T00:00');
    row.children[1].children[0].value = '2026-10-17T22:17';
    await row.children[1].children[0].dispatchEvent({ type: 'input' });
    const bob = row.children[2].children[1].children[0]; bob.checked = true; await bob.dispatchEvent({ type: 'change' });
    await ui.preview();
    assert.equal(ui.elements['oncall-schedule-warnings'].children[0].textContent, 'Coverage gap');
    assert.equal(ui.elements['oncall-warning-lane'].children.length, 1);
    await ui.elements['oncall-save-schedule'].click();
    const save = ui.requests.find(request => request.url === '/save');
    assert.equal(save.body.slots[0].endLocal, '2026-10-17T22:17');
    assert.equal(save.body.slots[0].startLocal, undefined);
    assert.equal(save.body.slots[0].startsAtUtc, start);
    assert.deepEqual(save.body.slots[0].userIds, ['a', 'b']);
    assert.equal(save.options.headers.RequestVerificationToken, 'csrf');
    assert.equal(ui.window.reloads, 1);
});
test('add, delete and reset preserve original drafts; chart is read-only and external bars survive redraw', async () => {
    const ui = setup(); await ui.flush();
    await ui.elements['oncall-add-shift'].click();
    assert.equal(ui.elements['oncall-shift-rows'].children.length, 2);
    const first = ui.elements['oncall-shift-rows'].children[0]; await first.children[3].children[0].click();
    assert.equal(ui.elements['oncall-shift-rows'].children.length, 1);
    await ui.preview();
    assert(ui.external.parent.children.includes(ui.external));
    await ui.elements['oncall-reset-schedule'].click(); await ui.flush();
    assert.equal(ui.elements['oncall-shift-rows'].children[0].children[1].children[0].value, '2026-10-15T22:00');
    assert.equal(ui.elements['oncall-gantt'].querySelectorAll('[data-draft-shift]')[0].dataset.readonly, 'true');
    assert.equal(ui.requests.filter(request => request.url === '/save').length, 0);
});
test('template replacement shows affected shifts before writing and cancel leaves database untouched', async () => {
    const ui = setup({ replace: true }); await ui.flush();
    await ui.elements['oncall-generate'].click();
    assert.equal(ui.elements['oncall-replace-confirm'].hidden, false);
    assert.match(ui.elements['oncall-replaced-shifts'].children[0].textContent, /Alice <team>/);
    assert.equal(ui.requests.filter(request => request.url === '/generate').length, 0);
    await ui.elements['oncall-replace-cancel'].click();
    await ui.elements['oncall-replace-apply'].click();
    assert.equal(ui.window.reloads, 0);
    await ui.elements['oncall-generate'].click(); await ui.elements['oncall-replace-apply'].click();
    assert.equal(ui.requests.find(request => request.url === '/generate').body.confirmReplace, true);
});
test('copy uses unsaved table edits, confirms replacement, and changing destination invalidates confirmation', async () => {
    const ui = setup({ replace: true }); await ui.flush();
    const input = ui.elements['oncall-shift-rows'].children[0].children[1].children[0]; input.value = '2026-10-16T22:17'; await input.dispatchEvent({ type: 'input' });
    await ui.elements['oncall-copy-range'].click();
    const copy = ui.requests.find(request => request.url === '/preview?generate=false');
    assert.equal(copy.body.slots[0].endLocal, '2026-10-23T22:17');
    await ui.elements['oncall-range-end'].dispatchEvent({ type: 'input' });
    await ui.elements['oncall-replace-apply'].click(); assert.equal(ui.window.reloads, 0);
    await ui.elements['oncall-copy-range'].click(); await ui.elements['oncall-replace-apply'].click();
    assert.equal(ui.requests.find(request => request.url === '/save').body.confirmReplace, true);
    assert.equal(ui.window.reloads, 0);
    assert.equal(ui.elements['oncall-schedule'].dataset.version, 'new-version');
    assert.equal(ui.elements['oncall-shift-rows'].children[0].children[1].children[0].value, '2026-10-16T22:17');
});
test('failed saves retain edits, expose server errors, and keep navigation protection', async () => {
    const ui = setup({ fail: true }); await ui.flush();
    await ui.elements['oncall-add-shift'].click(); await ui.elements['oncall-save-schedule'].click();
    assert.equal(ui.window.reloads, 0);
    assert.equal(ui.elements['oncall-shift-rows'].children.length, 2);
    assert.equal(ui.elements['oncall-schedule-errors'].children[0].textContent, 'Version conflict');
    let prevented = false; ui.window.listeners.beforeunload({ preventDefault() { prevented = true; } }); assert(prevented);
    assert.equal(ui.elements['oncall-save-schedule'].disabled, false);
});

test('duration units use their own localized label without reusing formatted count messages', async () => {
    const ui = setup({ messages: { 'oncall-hours-unit': '小时', hours: '{0} 小时' } }); await ui.flush();
    assert.equal(ui.elements['oncall-gantt'].children[0].children[0].textContent, '94.00 小时');
    const markup = fs.readFileSync(require.resolve('../../src/Aiursoft.AlertCenter/Views/Shared/_ActionFeedback.cshtml'), 'utf8');
    assert.match(markup, /data-key="oncall-hours-unit">@viewLocalizer\["hours"\]/);
});
