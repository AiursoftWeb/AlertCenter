const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const test = require('node:test');

const root = path.resolve(__dirname, '../../src/Aiursoft.AlertCenter');
const script = fs.readFileSync(path.join(root, 'wwwroot/scripts/clipboard.js'), 'utf8');

function setup({ writeText = async () => {}, text = '  示例 <code>\n第二行\n', copyText,
    clipboard = true, source = true, disabled = false, busy = null, feedback = true } = {}) {
    const attrs = new Map(busy === null ? [] : [['aria-busy', busy]]);
    const icon = { tagName: 'svg' };
    let click;
    const button = {
        disabled,
        dataset: { copyTarget: 'source', copyFeedback: 'feedback' },
        childNodes: [icon, { textContent: '复制代码' }],
        getAttribute: name => attrs.get(name) ?? null,
        setAttribute: (name, value) => attrs.set(name, value),
        removeAttribute: name => attrs.delete(name),
        addEventListener: (name, handler) => {
            assert.equal(name, 'click');
            click = handler;
        }
    };
    if (copyText !== undefined) button.dataset.copyText = copyText;
    Object.defineProperty(button, 'innerHTML', {
        get: () => '<svg data-lucide="copy"></svg>复制代码',
        set: () => assert.fail('Copy interactions must not replace button markup or SVG icons')
    });
    const feedbackElement = {
        children: [],
        replaceChildren(...children) { this.children = children; }
    };
    const document = {
        querySelectorAll: () => [button],
        getElementById: id => id === 'feedback' ? (feedback ? feedbackElement : null) :
            id === 'source' && source ? { textContent: text } : null,
        createElement: tagName => ({
            tagName,
            children: [],
            appendChild(child) { this.children.push(child); }
        })
    };
    vm.runInNewContext(script, { document, navigator: clipboard ? { clipboard: { writeText } } : {} });
    return {
        button, icon, click,
        alert: () => feedbackElement.children[0],
        message: () => feedbackElement.children[0]?.children[0]?.textContent ?? ''
    };
}

test('waits for clipboard confirmation, blocks duplicate clicks, and preserves the original SVG', async () => {
    let resolve;
    const pending = new Promise(done => { resolve = done; });
    const writes = [];
    const ui = setup({ writeText: text => { writes.push(text); return pending; } });
    const originalChildren = ui.button.childNodes;
    const operation = ui.click();
    assert.deepEqual(writes, ['  示例 <code>\n第二行\n']);
    assert.equal(ui.button.disabled, true);
    assert.equal(ui.button.getAttribute('aria-busy'), 'true');
    assert.equal(ui.alert().className, 'alert alert-info mb-3');
    assert.equal(ui.message(), '正在复制…');
    await ui.click();
    assert.equal(writes.length, 1);
    resolve();
    await operation;
    assert.equal(ui.alert().className, 'alert alert-success mb-3');
    assert.equal(ui.alert().children[0].className, 'alert-message');
    assert.equal(ui.message(), '已复制到剪贴板。');
    assert.equal(ui.button.disabled, false);
    assert.equal(ui.button.getAttribute('aria-busy'), null);
    assert.equal(ui.button.childNodes, originalChildren);
    assert.equal(ui.button.childNodes[0], ui.icon);
});

test('copies the complete literal delivery ID instead of a truncated display', async () => {
    const writes = [];
    const id = 'c750ba2a-48df-47da-81dc-128b6f6f2720';
    const ui = setup({ copyText: id, text: 'c750ba2a...', writeText: async text => { writes.push(text); } });
    await ui.click();
    assert.deepEqual(writes, [id]);
    assert.equal(ui.alert().className, 'alert alert-success mb-3');
});

test('denied clipboard access reports failure, restores state, and permits a successful retry', async () => {
    let attempts = 0;
    const ui = setup({
        busy: 'false',
        writeText: async () => {
            attempts++;
            if (attempts === 1) throw new Error('NotAllowedError');
        }
    });
    await ui.click();
    assert.equal(ui.alert().className, 'alert alert-danger mb-3');
    assert.match(ui.message(), /复制失败.*手动选择并复制.*剪贴板权限/);
    assert(!ui.message().includes('已复制'));
    assert.equal(ui.button.disabled, false);
    assert.equal(ui.button.getAttribute('aria-busy'), 'false');
    await ui.click();
    assert.equal(attempts, 2);
    assert.equal(ui.alert().className, 'alert alert-success mb-3');
});

test('handles unavailable Clipboard API without claiming success', async () => {
    const ui = setup({ clipboard: false });
    await ui.click();
    assert.equal(ui.alert().className, 'alert alert-danger mb-3');
    assert.match(ui.message(), /复制失败/);
    assert.equal(ui.button.disabled, false);
    assert.equal(ui.button.getAttribute('aria-busy'), null);
});

test('handles a synchronous browser clipboard error', async () => {
    const ui = setup({ writeText: () => { throw new Error('SecurityError'); } });
    await ui.click();
    assert.equal(ui.alert().className, 'alert alert-danger mb-3');
    assert.equal(ui.button.disabled, false);
});

for (const options of [{ source: false }, { text: '' }]) {
    test(`rejects ${options.source === false ? 'missing' : 'empty'} content without writing`, async () => {
        let writes = 0;
        const ui = setup({ ...options, writeText: async () => { writes++; } });
        await ui.click();
        assert.equal(writes, 0);
        assert.equal(ui.alert().className, 'alert alert-danger mb-3');
        assert.equal(ui.button.disabled, false);
    });
}

test('leaves an initially disabled copy button disabled', async () => {
    let writes = 0;
    const ui = setup({ disabled: true, writeText: async () => { writes++; } });
    await ui.click();
    assert.equal(writes, 0);
    assert.equal(ui.button.disabled, true);
    assert.equal(ui.message(), '');
});

test('does not bind a copy button without its feedback region', () => {
    assert.equal(setup({ feedback: false }).click, undefined);
});

for (const [view, expectedButtons] of [
    ['Views/Alerts/GettingStarted.cshtml', 4],
    ['Views/Push/Notification.cshtml', 1],
    ['Views/Push/Delivery.cshtml', 1]
]) {
    test(`${view} wires copy buttons to an accessible UiStack feedback region`, () => {
        const markup = fs.readFileSync(path.join(root, view), 'utf8');
        assert(!markup.includes('navigator.clipboard'));
        assert(!markup.includes('copyCode('));
        assert.match(markup, /@section scripts\s*\{\s*<script src="~\/scripts\/clipboard\.js" asp-append-version="true"><\/script>/);
        const buttons = [...markup.matchAll(/<button\b[^>]*\bdata-copy-(?:target|text)="[^"]+"[^>]*>/g)];
        assert.equal(buttons.length, expectedButtons);
        for (const [button] of buttons) {
            assert.match(button, /type="button"/);
            const feedbackId = /data-copy-feedback="([^"]+)"/.exec(button)?.[1];
            assert(feedbackId);
            assert(markup.includes(`id="${feedbackId}"`));
            const region = new RegExp(`<div id="${feedbackId}"[^>]*>`).exec(markup)?.[0];
            assert.match(region, /role="status"/);
            assert.match(region, /aria-live="polite"/);
            assert.match(region, /aria-atomic="true"/);
            const sourceId = /data-copy-target="([^"]+)"/.exec(button)?.[1];
            if (sourceId) assert(markup.includes(`id="${sourceId}"`));
        }
        if (view.endsWith('Delivery.cshtml')) assert.match(buttons[0][0], /aria-label="复制完整投递 ID"/);
        if (view.endsWith('Notification.cshtml')) {
            assert.match(markup, /<form asp-action="MarkRead"[^>]*\bdata-action-feedback\b/);
        }
    });
}
