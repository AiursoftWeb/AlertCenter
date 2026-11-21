const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const test = require('node:test');

const scripts = ['oncall-group-form.js', 'oncall-member-search.js'].map(file => fs.readFileSync(path.resolve(__dirname, '../../src/Aiursoft.AlertCenter/wwwroot/scripts', file), 'utf8'));
const people = ['Alice', 'Bob', 'Casey', 'Dana'].map((name, index) => ({ id: String(index + 1), name, username: name.toLowerCase() }));

function setup(options = {}) {
    const elements = {}, requests = [], deferred = [], timers = new Map(), pending = [], captures = new Set();
    let document, hitTarget, nextTimer = 0;
    class BrowserEvent {
        constructor(type, init = {}) {
            Object.assign(this, { type, bubbles: false, defaultPrevented: false, button: 0, pointerId: 1, pointerType: 'mouse', clientX: 0, clientY: 0 }, init);
        }
        preventDefault() { this.defaultPrevented = true; }
    }
    class Element {
        constructor(tag = 'div') {
            this.tagName = tag.toUpperCase(); this.children = []; this.parentElement = null; this.listeners = {};
            this.dataset = {}; this.attributes = {}; this.className = ''; this.value = ''; this.disabled = false;
            this.hidden = false; this.checked = false; this.readOnly = false; this.open = false; this._text = '';
            this.classList = {
                contains: value => this.className.split(/\s+/).includes(value),
                add: (...values) => { this.className = [...new Set([...this.className.split(/\s+/).filter(Boolean), ...values])].join(' '); },
                remove: (...values) => { this.className = this.className.split(/\s+/).filter(value => !values.includes(value)).join(' '); },
                toggle: (value, force) => { const add = force ?? !this.classList.contains(value); this.classList[add ? 'add' : 'remove'](value); return add; }
            };
        }
        get textContent() { return this._text + this.children.map(child => child.textContent).join(''); }
        set textContent(value) { this.replaceChildren(); this._text = String(value); }
        append(...children) { for (const child of children) { child.remove(); child.parentElement = this; this.children.push(child); } }
        remove() { if (this.parentElement) this.parentElement.children = this.parentElement.children.filter(child => child !== this); this.parentElement = null; }
        replaceChildren(...children) { for (const child of this.children) child.parentElement = null; this.children = []; this._text = ''; this.append(...children); }
        setAttribute(key, value) { this.attributes[key] = String(value); }
        getAttribute(key) {
            if (key.startsWith('data-')) return this.dataset[key.slice(5).replace(/-([a-z])/g, (_, letter) => letter.toUpperCase())] ?? null;
            return this.attributes[key] ?? this[key] ?? null;
        }
        matches(selector) {
            if (selector === ':invalid') return !!this.invalid;
            const attribute = selector.match(/^\[([^=\]]+)(?:="([^"]*)")?\]$/);
            if (attribute) return this.getAttribute(attribute[1]) !== null && (attribute[2] === undefined || this.getAttribute(attribute[1]) === attribute[2]);
            if (selector.startsWith('.')) return this.classList.contains(selector.slice(1));
            return this.tagName === selector.toUpperCase();
        }
        closest(selector) { for (let node = this; node; node = node.parentElement) if (node.matches(selector)) return node; return null; }
        querySelectorAll(selector) { return this.children.flatMap(child => [...(child.matches(selector) ? [child] : []), ...child.querySelectorAll(selector)]); }
        querySelector(selector) { return this.querySelectorAll(selector)[0] || null; }
        addEventListener(type, handler) { (this.listeners[type] ??= []).push(handler); }
        dispatchEvent(event) {
            event.target ??= this;
            for (let node = this; node; node = event.bubbles ? node.parentElement : null) {
                for (const handler of node.listeners[event.type] || []) {
                    const result = handler(event); if (result?.then) pending.push(result);
                }
            }
            return !event.defaultPrevented;
        }
        click() { if (!this.disabled) this.dispatchEvent(new BrowserEvent('click', { bubbles: true })); }
        focus() { document.activeElement = this; }
        setPointerCapture(id) { captures.add(id); }
        hasPointerCapture(id) { return captures.has(id); }
        releasePointerCapture(id) { captures.delete(id); this.dispatchEvent(new BrowserEvent('lostpointercapture', { pointerId: id })); }
    }
    document = new Element('document'); document.activeElement = null;
    document.createElement = tag => new Element(tag);
    document.getElementById = id => elements[id] || null;
    document.elementFromPoint = () => hitTarget;
    function create(id, tag = 'div', parent = document) {
        const element = new Element(tag); element.id = id; elements[id] = element; parent.append(element); return element;
    }
    const form = create('oncall-group-form', 'form');
    form.dataset = { searchUrl: '/search', previewUrl: '/preview', groupId: options.groupId || '' };
    const csrf = create('csrf', 'input', form); csrf.name = '__RequestVerificationToken'; csrf.value = 'csrf';
    const selected = create('oncall-selected', 'ol', form), fallback = create('oncall-fallback-selected', 'ul', form);
    for (const [users, list] of [[options.members || [], selected], [options.fallback || [], fallback]]) {
        for (const user of users) { const item = new Element('li'); item.dataset = { userId: user.id, name: user.name, username: user.username }; list.append(item); }
    }
    for (const user of options.original || []) { const original = new Element(); original.dataset.originalMemberId = user.id; form.append(original); }
    for (const id of ['reorder-status', 'removal-warning', 'members-empty', 'fallback-warning', 'anchor-notice', 'anchor-suggestions', 'preview-status', 'preview-zone', 'preview-warnings']) create(`oncall-${id}`, 'div', form);
    create('oncall-group-preview', 'ol', form);
    for (const id of ['GroupInput_RuleId', 'GroupInput_TimeZoneId', 'oncall-role']) create(id, 'select', form);
    for (const id of ['GroupInput_AnchorLocal', 'oncall-anchor-manual', 'oncall-autoextend', 'oncall-user-search', 'oncall-fallback-search']) create(id, 'input', form);
    elements.GroupInput_RuleId.value = options.template ?? 'weekly';
    elements.GroupInput_TimeZoneId.value = options.zone || 'Asia/Shanghai';
    elements.GroupInput_AnchorLocal.value = options.anchor || '';
    elements.GroupInput_AnchorLocal.readOnly = !!options.locked;
    elements['oncall-anchor-manual'].value = options.manual ? 'true' : 'false';
    elements['oncall-autoextend'].checked = !!options.autoExtend;
    elements['oncall-anchor-notice'].hidden = true;
    for (const prefix of ['oncall', 'oncall-fallback']) {
        for (const suffix of ['search-status', 'search-results', 'search-pagination', 'page']) create(`${prefix}-${suffix}`, 'div', form);
        for (const suffix of ['search-button', 'prev', 'next']) create(`${prefix}-${suffix}`, 'button', form);
    }
    function previewResponse(body) {
        const automatic = body.ruleId === 'weekly' ? '2026-11-09T22:00' : '2026-11-09T11:00';
        const anchorLocal = options.locked ? (body.timeZoneId === 'UTC' ? '2026-11-09T14:00' : '2026-11-09T22:00') : body.anchorLocal || automatic;
        return { anchorLocal, suggestedAnchorLocal: automatic, anchorLocked: !!options.locked,
            suggestions: [{ anchorLocal: '2026-11-13T22:00' }, { anchorLocal: '2026-11-16T22:00' }], warnings: [], errors: [],
            slots: body.ruleId && body.userIds.length ? Array.from({ length: 4 }, (_, index) => ({
                startLocal: index ? '2026-11-13T22:00' : anchorLocal, endLocal: '2026-11-16T22:00', userIds: [body.userIds[index % body.userIds.length]]
            })) : [] };
    }
    const sandbox = { document, window: { location: { origin: 'https://example.invalid' } }, URL, CustomEvent: BrowserEvent,
        getLocalizedText: (_, fallback) => fallback,
        setTimeout(handler) { const id = ++nextTimer; timers.set(id, handler); return id; }, clearTimeout(id) { timers.delete(id); },
        fetch: async (url, requestOptions = {}) => {
            const body = requestOptions.body ? JSON.parse(requestOptions.body) : null;
            const request = { url: String(url), body, options: requestOptions }; requests.push(request);
            if (options.defer && (body ? options.defer === 'preview' : options.defer === 'search')) {
                return new Promise(resolve => deferred.push({ request, resolve: (data, ok = true) => resolve({ ok, json: async () => data }) }));
            }
            const data = body ? previewResponse(body) : { users: people, hasMore: new URL(url).searchParams.get('page') === '1' };
            return { ok: true, json: async () => data };
        }
    };
    let jqueryChange;
    if (options.jquery) sandbox.$ = () => ({ select2() { return this; }, on(_, handler) { jqueryChange = handler; return this; } });
    vm.runInNewContext(scripts.join('\n'), sandbox);
    return {
        elements, form, document, requests, deferred, selected, fallback,
        async flush() { for (let index = 0; index < 10; index++) await Promise.resolve(); await Promise.all(pending.splice(0)); },
        fire(element, type, init) { const event = new BrowserEvent(type, { bubbles: true, ...init }); element.dispatchEvent(event); return event; },
        change(id, value) { elements[id].value = value; elements[id].dispatchEvent(new BrowserEvent('change', { bubbles: true })); },
        pointer(type, target, init = {}) { target.dispatchEvent(new BrowserEvent(type, { bubbles: true, ...init })); },
        hit(element) { hitTarget = element; },
        hold() { for (const [id, handler] of [...timers]) { timers.delete(id); handler(); } },
        jqueryChange() { jqueryChange(); },
        ids: () => selected.children.map(row => row.dataset.userId),
        inputFor(prefix, id) { return elements[`${prefix}-search-results`].querySelectorAll('input').find(input => input.dataset.userId === id); },
        choose(prefix, id) { const input = this.inputFor(prefix, id); input.checked = true; this.fire(input, 'change'); },
        row(id) { return selected.children.find(row => row.dataset.userId === id); },
        control(id, action) { return this.row(id).querySelector(`[data-member-action="${action}"]`); }
    };
}

async function search(ui, prefix = 'oncall', query = 'a') {
    ui.elements[prefix === 'oncall' ? 'oncall-user-search' : 'oncall-fallback-search'].value = query;
    ui.elements[`${prefix}-search-button`].click(); await ui.flush();
}

test('independent member and fallback searches support continuous adds, duplicates, filters and pagination', async () => {
    const ui = setup(); await ui.flush();
    await search(ui);
    ui.choose('oncall', '1'); ui.choose('oncall', '2'); await ui.flush();
    assert.deepEqual(ui.ids(), ['1', '2']);
    assert.equal(ui.inputFor('oncall', '1').disabled, true);
    assert.equal(ui.inputFor('oncall', '1').checked, true);
    assert.match(ui.elements['oncall-search-results'].textContent, /Already selected/);
    ui.choose('oncall', '1'); assert.deepEqual(ui.ids(), ['1', '2']);
    assert.deepEqual(ui.selected.querySelectorAll('.oncall-member-id').map(input => input.value), ['1', '2']);
    const memberResults = ui.elements['oncall-search-results'].textContent;
    await search(ui, 'oncall-fallback', 'alice');
    ui.choose('oncall-fallback', '1'); ui.choose('oncall-fallback', '4');
    assert.deepEqual(ui.fallback.children.map(row => row.dataset.userId), ['1', '4']);
    assert.deepEqual(ui.ids(), ['1', '2']);
    assert.equal(ui.elements['oncall-fallback-warning'].hidden, true);
    assert.equal(ui.elements['oncall-search-results'].textContent, memberResults);
    ui.change('oncall-role', 'operators'); await ui.flush();
    let request = new URL(ui.requests.at(-1).url);
    assert.equal(request.searchParams.get('roleId'), 'operators');
    assert.equal(request.searchParams.get('query'), '');
    ui.elements['oncall-next'].click(); await ui.flush();
    request = new URL(ui.requests.at(-1).url);
    assert.equal(request.searchParams.get('page'), '2');
    assert.equal(request.searchParams.get('roleId'), 'operators');
    assert.equal(ui.elements['oncall-fallback-page'].textContent, 'Page 1');
    ui.fallback.children[0].querySelector('button').click();
    assert.equal(ui.inputFor('oncall-fallback', '1').disabled, false);
    assert.equal(ui.inputFor('oncall', '1').disabled, true);
    ui.fallback.children[0].querySelector('button').click();
    assert.equal(ui.elements['oncall-fallback-warning'].hidden, false);
});

test('search results discard stale requests independently for each search', async () => {
    const ui = setup({ defer: 'search' }); await ui.flush();
    ui.elements['oncall-search-button'].click(); ui.elements['oncall-search-button'].click();
    ui.elements['oncall-fallback-search-button'].click();
    ui.deferred[1].resolve({ users: [people[1]], hasMore: false });
    ui.deferred[2].resolve({ users: [people[3]], hasMore: false }); await ui.flush();
    ui.deferred[0].resolve({ users: [people[0]], hasMore: true }); await ui.flush();
    assert.equal(ui.elements['oncall-search-results'].querySelector('input').dataset.userId, '2');
    assert.equal(ui.elements['oncall-fallback-search-results'].querySelector('input').dataset.userId, '4');
});

test('move buttons and keyboard retain focus, announce order, update preview and retain departed warnings', async () => {
    const ui = setup({ members: people.slice(0, 3), original: people.slice(0, 3) }); await ui.flush();
    ui.control('1', 'down').click(); await ui.flush();
    assert.deepEqual(ui.ids(), ['2', '1', '3']);
    assert.equal(ui.document.activeElement, ui.control('1', 'down'));
    assert.equal(ui.elements['oncall-reorder-status'].textContent, 'Alice moved to position 2 of 3.');
    assert.deepEqual(ui.requests.at(-1).body.userIds, ['2', '1', '3']);
    assert.match(ui.elements['oncall-group-preview'].children[0].textContent, /^Bob/);
    ui.control('1', 'down').click();
    assert.equal(ui.document.activeElement, ui.control('1', 'handle'));
    ui.control('1', 'handle').focus();
    const event = ui.fire(ui.control('1', 'handle'), 'keydown', { key: 'ArrowUp' }); await ui.flush();
    assert.equal(event.defaultPrevented, true);
    assert.deepEqual(ui.ids(), ['2', '1', '3']);
    assert.equal(ui.document.activeElement, ui.control('1', 'handle'));
    ui.control('1', 'remove').click(); await ui.flush();
    assert.equal(ui.elements['oncall-removal-warning'].classList.contains('d-none'), false);
    assert.deepEqual(ui.requests.at(-1).body.userIds, ['2', '3']);
    await search(ui); ui.choose('oncall', '1'); await ui.flush();
    assert.equal(ui.elements['oncall-removal-warning'].classList.contains('d-none'), true);
});

test('pointer drag commits on release and supports cancellation without disturbing the queue', async () => {
    const ui = setup({ members: people.slice(0, 3) }); await ui.flush();
    ui.pointer('pointerdown', ui.control('1', 'handle'));
    ui.hit(ui.row('3'));
    ui.pointer('pointermove', ui.selected, { clientY: 100 });
    assert.deepEqual(ui.ids(), ['1', '2', '3']);
    assert.equal(ui.row('3').classList.contains('oncall-drop-target'), true);
    ui.pointer('pointerup', ui.selected); await ui.flush();
    assert.deepEqual(ui.ids(), ['2', '3', '1']);
    assert.equal(ui.document.activeElement, ui.control('1', 'handle'));
    ui.pointer('pointerdown', ui.control('1', 'handle'));
    ui.hit(ui.row('2')); ui.pointer('pointermove', ui.selected, { clientY: 100 });
    ui.fire(ui.control('1', 'handle'), 'keydown', { key: 'Escape' });
    ui.pointer('pointerup', ui.selected);
    assert.deepEqual(ui.ids(), ['2', '3', '1']);
    ui.pointer('pointerdown', ui.control('1', 'handle'));
    ui.pointer('pointermove', ui.selected, { clientY: 100 }); ui.pointer('pointercancel', ui.selected);
    ui.pointer('pointerup', ui.selected);
    assert.deepEqual(ui.ids(), ['2', '3', '1']);
});

test('touch reorder requires long press, cancels early movement, and ignores other pointer ids', async () => {
    const ui = setup({ members: people.slice(0, 3) }); await ui.flush();
    ui.pointer('pointerdown', ui.control('1', 'handle'), { pointerType: 'touch' });
    ui.hit(ui.row('3')); ui.pointer('pointermove', ui.selected, { pointerType: 'touch', clientY: 100 });
    ui.hold(); ui.pointer('pointerup', ui.selected);
    assert.deepEqual(ui.ids(), ['1', '2', '3']);
    ui.pointer('pointerdown', ui.control('1', 'handle'), { pointerType: 'touch' }); ui.hold();
    assert.equal(ui.row('1').classList.contains('oncall-dragging'), true);
    ui.pointer('pointermove', ui.selected, { pointerType: 'touch', clientY: 100 });
    ui.pointer('pointerup', ui.selected, { pointerId: 2 });
    ui.pointer('pointercancel', ui.selected, { pointerId: 2 });
    assert.deepEqual(ui.ids(), ['1', '2', '3']);
    ui.pointer('pointerup', ui.selected); await ui.flush();
    assert.deepEqual(ui.ids(), ['2', '3', '1']);
    assert.equal(ui.document.activeElement, ui.control('1', 'handle'));
});

test('no template disables and clears automatic extension and has no preview slots', async () => {
    const ui = setup({ members: people.slice(0, 2), template: '', autoExtend: true }); await ui.flush();
    assert.equal(ui.elements['oncall-autoextend'].disabled, true);
    assert.equal(ui.elements['oncall-autoextend'].checked, false);
    assert.equal(ui.elements['oncall-group-preview'].children.length, 0);
    assert.equal(ui.elements['oncall-anchor-notice'].hidden, true);
    assert.match(ui.elements['oncall-preview-status'].textContent, /Choose a scheduling template/);
    ui.change('GroupInput_RuleId', 'fixed'); await ui.flush();
    assert.equal(ui.elements['oncall-autoextend'].disabled, false);
    ui.elements['oncall-autoextend'].checked = true;
    ui.change('GroupInput_RuleId', ''); await ui.flush();
    assert.equal(ui.elements['oncall-autoextend'].checked, false);
    assert.equal(ui.elements['oncall-anchor-notice'].hidden, true);
});

test('weekly and fixed defaults use server suggestions and recompute on timezone and template changes', async () => {
    const ui = setup({ members: people.slice(0, 3) }); await ui.flush();
    assert.equal(ui.requests[0].body.anchorLocal, null);
    assert.equal(ui.requests[0].options.headers.RequestVerificationToken, 'csrf');
    assert.equal(ui.elements.GroupInput_AnchorLocal.value, '2026-11-09T22:00');
    assert.equal(ui.elements['oncall-anchor-notice'].hidden, false);
    assert.equal(ui.elements['oncall-group-preview'].children.length, 4);
    ui.change('GroupInput_RuleId', 'fixed'); await ui.flush();
    assert.equal(ui.requests.at(-1).body.anchorLocal, null);
    assert.equal(ui.elements.GroupInput_AnchorLocal.value, '2026-11-09T11:00');
    ui.change('GroupInput_TimeZoneId', 'America/New_York'); await ui.flush();
    assert.equal(ui.requests.at(-1).body.anchorLocal, null);
    assert.equal(ui.requests.at(-1).body.timeZoneId, 'America/New_York');
    assert.equal(ui.elements['oncall-preview-zone'].textContent, 'America/New_York');
    const enhanced = setup({ jquery: true }); await enhanced.flush();
    enhanced.elements.GroupInput_TimeZoneId.value = 'UTC'; enhanced.jqueryChange(); await enhanced.flush();
    assert.equal(enhanced.requests.at(-1).body.timeZoneId, 'UTC');
});

test('manual edits and shortcuts survive template/timezone changes and validation redisplays', async () => {
    const ui = setup({ members: people.slice(0, 2) }); await ui.flush();
    ui.elements.GroupInput_AnchorLocal.value = '2027-01-02T03:04';
    ui.fire(ui.elements.GroupInput_AnchorLocal, 'input'); await ui.flush();
    assert.equal(ui.elements['oncall-anchor-manual'].value, 'true');
    assert.equal(ui.elements['oncall-anchor-notice'].hidden, true);
    ui.change('GroupInput_RuleId', 'fixed'); ui.change('GroupInput_TimeZoneId', 'UTC'); await ui.flush();
    assert.equal(ui.elements.GroupInput_AnchorLocal.value, '2027-01-02T03:04');
    assert.equal(ui.requests.at(-1).body.anchorLocal, '2027-01-02T03:04');
    ui.elements['oncall-anchor-suggestions'].children[0].click(); await ui.flush();
    assert.equal(ui.elements.GroupInput_AnchorLocal.value, '2026-11-13T22:00');
    ui.change('GroupInput_RuleId', 'weekly'); await ui.flush();
    assert.equal(ui.requests.at(-1).body.anchorLocal, '2026-11-13T22:00');
    const redisplay = setup({ manual: true, anchor: '2027-01-02T03:04' }); await redisplay.flush();
    redisplay.change('GroupInput_TimeZoneId', 'UTC'); await redisplay.flush();
    assert.equal(redisplay.requests.at(-1).body.anchorLocal, '2027-01-02T03:04');
});

test('existing edit anchors are preserved on load and locked anchors update local display through the API', async () => {
    const edit = setup({ groupId: 'group', anchor: '2020-01-01T00:00' }); await edit.flush();
    assert.equal(edit.requests[0].body.anchorLocal, '2020-01-01T00:00');
    assert.equal(edit.elements.GroupInput_AnchorLocal.value, '2020-01-01T00:00');
    assert.equal(edit.elements['oncall-anchor-notice'].hidden, true);
    const locked = setup({ groupId: 'group', anchor: '2026-11-09T22:00', locked: true }); await locked.flush();
    locked.change('GroupInput_TimeZoneId', 'UTC'); await locked.flush();
    assert.equal(locked.requests.at(-1).body.anchorLocal, null);
    assert.equal(locked.elements.GroupInput_AnchorLocal.value, '2026-11-09T14:00');
    assert.equal(locked.elements.GroupInput_AnchorLocal.readOnly, true);
    assert.equal(locked.elements['oncall-anchor-suggestions'].children.length, 0);
});

test('validation reveals both nested notification settings and the collapsed advanced section', async () => {
    const ui = setup(); await ui.flush();
    const advanced = ui.document.createElement('details'), notification = ui.document.createElement('details');
    const field = ui.document.createElement('textarea'); field.invalid = true;
    ui.form.append(advanced); advanced.append(notification); notification.append(field);
    ui.fire(field, 'invalid');
    assert.equal(advanced.open, true); assert.equal(notification.open, true);
    advanced.open = notification.open = false;
    ui.fire(ui.form, 'submit');
    assert.equal(advanced.open, true); assert.equal(notification.open, true);
});

test('membership changes during an automatic recompute keep requesting the new default', async () => {
    const ui = setup({ members: people.slice(0, 2), anchor: '2020-01-01T00:00', defer: 'preview' });
    ui.deferred[0].resolve({ anchorLocal: '2020-01-01T00:00', slots: [], errors: [] }); await ui.flush();
    ui.change('GroupInput_RuleId', 'fixed');
    ui.control('1', 'down').click();
    assert.equal(ui.requests.at(-1).body.anchorLocal, null);
    ui.deferred[2].resolve({ anchorLocal: '2026-11-09T11:00', slots: [], errors: [] }); await ui.flush();
    ui.deferred[1].resolve({ anchorLocal: '2026-11-09T12:00', slots: [], errors: [] }); await ui.flush();
    assert.equal(ui.elements.GroupInput_AnchorLocal.value, '2026-11-09T11:00');
});

test('stale previews cannot overwrite edits and failed previews clear obsolete slots', async () => {
    const ui = setup({ members: people.slice(0, 2), defer: 'preview' });
    ui.elements.GroupInput_AnchorLocal.value = '2027-01-02T03:04'; ui.fire(ui.elements.GroupInput_AnchorLocal, 'input');
    ui.deferred[0].resolve({ anchorLocal: '2026-11-09T22:00', slots: [], errors: [] }); await ui.flush();
    assert.equal(ui.elements.GroupInput_AnchorLocal.value, '2027-01-02T03:04');
    ui.deferred[1].resolve({ anchorLocal: '2027-01-02T03:04', slots: [{ startLocal: '2027-01-02T03:04', endLocal: '2027-01-03T03:04', userIds: ['1'] }], errors: [], warnings: ['Reminder'] }); await ui.flush();
    assert.equal(ui.elements['oncall-group-preview'].children.length, 1);
    ui.control('1', 'down').click();
    assert.equal(ui.elements['oncall-group-preview'].children.length, 0);
    ui.deferred[2].resolve({ errors: ['Invalid team timezone'] }, false); await ui.flush();
    assert.equal(ui.elements['oncall-group-preview'].children.length, 0);
    assert.equal(ui.elements['oncall-preview-status'].textContent, 'Invalid team timezone');
    assert.equal(ui.elements['oncall-preview-warnings'].hidden, true);
    ui.elements.GroupInput_AnchorLocal.value = ''; ui.fire(ui.elements.GroupInput_AnchorLocal, 'input');
    assert.match(ui.elements['oncall-preview-status'].textContent, /Enter a scheduling start point/);
    assert.equal(ui.elements['oncall-group-preview'].getAttribute('aria-busy'), 'false');
});
