const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const test = require('node:test');

const script = fs.readFileSync(path.resolve(__dirname,
    '../../src/Aiursoft.AlertCenter/wwwroot/scripts/action-feedback.js'), 'utf8');

function setup({ status = 409, message = '版本冲突', sourceModal = false, pending = false } = {}) {
    const listeners = new Map();
    function element() {
        const events = new Map();
        const attributes = new Map();
        const classes = new Set();
        return {
            textContent: '', isConnected: true, disabled: false,
            classList: {
                add: name => classes.add(name), remove: name => classes.delete(name),
                contains: name => classes.has(name),
                toggle(name, force) { if (force) classes.add(name); else classes.delete(name); }
            },
            addEventListener(name, handler, options) {
                const handlers = events.get(name) || [];
                handlers.push({ handler, once: options?.once });
                events.set(name, handlers);
            },
            removeEventListener(name, handler) {
                events.set(name, (events.get(name) || []).filter(entry => entry.handler !== handler));
            },
            emit(name, event = {}) {
                for (const entry of [...(events.get(name) || [])]) {
                    if (entry.once) this.removeEventListener(name, entry.handler);
                    entry.handler(event);
                }
            },
            focus() { active = this; },
            setAttribute: (name, value) => attributes.set(name, value),
            getAttribute: name => attributes.get(name) ?? null,
            hasAttribute: name => attributes.has(name),
            removeAttribute: name => attributes.delete(name)
        };
    }
    let active;
    const ids = ['action-feedback', 'action-feedback-title', 'action-feedback-message',
        'action-feedback-refresh', 'action-feedback-return'];
    const elements = Object.fromEntries(ids.map(id => [id, element()]));
    const dialog = elements['action-feedback'];
    const refresh = elements['action-feedback-refresh'];
    const back = elements['action-feedback-return'];
    back.setAttribute('data-bs-dismiss', 'modal');
    const parent = sourceModal ? element() : null;
    parent?.classList.add('show');
    const submitter = element();
    const disabledButton = element();
    disabledButton.disabled = true;
    class Form {
        method = 'post';
        action = 'https://alertcenter.test/OnCall/DeleteGroup';
        dataset = {};
        hasAttribute(name) { return name === 'data-action-feedback'; }
        reportValidity() { return true; }
        closest() { return parent; }
        querySelectorAll() { return [submitter, disabledButton]; }
        setAttribute(name, value) { this[name] = value; }
        removeAttribute(name) { delete this[name]; }
    }
    const form = new Form();
    const modals = new Map();
    const bootstrap = {
        Modal: {
            getOrCreateInstance(target) {
                if (!modals.has(target)) modals.set(target, {
                    show() { target.classList.add('show'); target.emit('shown.bs.modal'); },
                    hide() { target.classList.remove('show'); target.emit('hidden.bs.modal'); }
                });
                return modals.get(target);
            }
        }
    };
    let reloads = 0;
    let fetches = 0;
    let complete;
    const response = { status, ok: status < 400, headers: { get: () => 'application/json' },
        json: async () => ({ message }) };
    const fetch = async () => {
        fetches++;
        return pending ? new Promise(resolve => { complete = () => resolve(response); }) : response;
    };
    const location = { href: 'https://alertcenter.test/OnCall/GroupDetails/1',
        origin: 'https://alertcenter.test', reload() { reloads++; } };
    const document = {
        getElementById: id => elements[id],
        addEventListener: (name, handler) => listeners.set(name, handler)
    };
    vm.runInNewContext(script, { document, window: { bootstrap, fetch, location }, bootstrap,
        fetch, URL, HTMLFormElement: Form, FormData: class {}, WeakSet });
    async function submit() {
        await listeners.get('submit')({ target: form, submitter, preventDefault() {} });
    }
    return { elements, dialog, refresh, back, parent, submitter, disabledButton, form, submit,
        complete: () => complete(), reloads: () => reloads, fetches: () => fetches, active: () => active };
}

test('conflict refresh requires explicit confirmation and cancellation retains error and draft', async () => {
    const ui = setup();
    await ui.submit();
    assert.equal(ui.elements['action-feedback-title'].textContent, '页面信息已更新');
    assert.equal(ui.elements['action-feedback-message'].textContent, '版本冲突');
    ui.refresh.emit('click');
    assert.equal(ui.reloads(), 0);
    assert.equal(ui.elements['action-feedback-title'].textContent, '确认刷新页面？');
    assert.equal(ui.back.getAttribute('data-bs-dismiss'), null);
    assert.equal(ui.active(), ui.back);
    let stopped = false;
    ui.back.emit('click', { stopPropagation() { stopped = true; } });
    assert(stopped);
    assert.equal(ui.reloads(), 0);
    assert.equal(ui.elements['action-feedback-message'].textContent, '版本冲突');
    assert.equal(ui.back.getAttribute('data-bs-dismiss'), 'modal');
    assert.equal(ui.active(), ui.refresh);
    assert(ui.dialog.classList.contains('show'));
    ui.refresh.emit('click');
    ui.refresh.emit('click');
    assert.equal(ui.reloads(), 1);
});

test('closing refresh confirmation restores its source dialog and starts fresh next time', async () => {
    const ui = setup({ sourceModal: true });
    await ui.submit();
    assert(!ui.parent.classList.contains('show'));
    assert(ui.dialog.classList.contains('show'));
    ui.refresh.emit('click');
    ui.dialog.emit('hidden.bs.modal');
    assert(ui.parent.classList.contains('show'));
    assert.equal(ui.active(), ui.submitter);
    assert.equal(ui.back.textContent, '返回修改');
    await ui.submit();
    ui.refresh.emit('click');
    assert.equal(ui.reloads(), 0);
    assert.equal(ui.back.textContent, '取消刷新');
});

test('non-conflict failures render plain text and keep refresh hidden', async () => {
    const ui = setup({ status: 400, message: '<script>not executable</script>' });
    await ui.submit();
    assert.equal(ui.elements['action-feedback-title'].textContent, '操作未完成');
    assert.equal(ui.elements['action-feedback-message'].textContent, '<script>not executable</script>');
    assert(ui.refresh.classList.contains('d-none'));
    ui.dialog.emit('hidden.bs.modal');
    assert.equal(ui.active(), ui.submitter);
});

test('submitting prevents duplicate requests and restores each button disabled state', async () => {
    const ui = setup({ pending: true });
    const first = ui.submit();
    assert.equal(ui.form['aria-busy'], 'true');
    assert.equal(ui.submitter.disabled, true);
    await ui.submit();
    assert.equal(ui.fetches(), 1);
    ui.complete();
    await first;
    assert.equal(ui.form['aria-busy'], undefined);
    assert.equal(ui.form.dataset.submitting, undefined);
    assert.equal(ui.submitter.disabled, false);
    assert.equal(ui.disabledButton.disabled, true);
});
