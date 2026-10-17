const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const test = require('node:test');

const root = path.resolve(__dirname, '../../src/Aiursoft.AlertCenter');
const localization = fs.readFileSync(path.join(root, 'wwwroot/scripts/localization.js'), 'utf8');
const script = fs.readFileSync(path.join(root, 'wwwroot/scripts/clipboard.js'), 'utf8');

function setup({ writeText = async () => {}, text = '  示例 <code>\n第二行\n', copyText,
    clipboard = true, source = true, disabled = false, busy = null, feedback = true, messages = {} } = {}) {
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
        querySelector: selector => {
            const key = selector.match(/data-key="([^"]+)"/)?.[1];
            return Object.hasOwn(messages, key) ? { textContent: messages[key] } : null;
        },
        querySelectorAll: () => [button],
        getElementById: id => id === 'feedback' ? (feedback ? feedbackElement : null) :
            id === 'source' && source ? { textContent: text } : null,
        createElement: tagName => ({
            tagName,
            children: [],
            appendChild(child) { this.children.push(child); }
        })
    };
    vm.runInNewContext(localization + '\n' + script, { document, navigator: clipboard ? { clipboard: { writeText } } : {} });
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
    assert.equal(ui.message(), 'Copying…');
    await ui.click();
    assert.equal(writes.length, 1);
    resolve();
    await operation;
    assert.equal(ui.alert().className, 'alert alert-success mb-3');
    assert.equal(ui.alert().children[0].className, 'alert-message');
    assert.equal(ui.message(), 'Copied to clipboard.');
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
    assert.match(ui.message(), /Copy failed.*select and copy.*clipboard permissions/);
    assert(!ui.message().includes('Copied to clipboard.'));
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
    assert.match(ui.message(), /Copy failed/);
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
    ['Views/Applications/GettingStarted.cshtml', 3],
    ['Views/Push/Notification.cshtml', 1],
    ['Views/Incidents/Details.cshtml', 2],
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
        if (view.endsWith('Delivery.cshtml')) assert.match(buttons[0][0], /aria-label="@Localizer\["Copy Full Delivery ID"\]"/);
        if (view.endsWith('Notification.cshtml')) {
            assert.match(markup, /<form asp-action="MarkRead"[^>]*\bdata-action-feedback\b/);
        }
    });
}

test('uses injected localized copy feedback without interpreting markup', async () => {
    const ui = setup({ messages: { 'copied-to-clipboard': '<已复制> $&' } });
    await ui.click();
    assert.equal(ui.message(), '<已复制> $&');
});

test('every JavaScript localization key has a literal server-rendered span', () => {
    const markup = fs.readFileSync(path.join(root, 'Views/Shared/_ActionFeedback.cshtml'), 'utf8');
    const supplied = new Map([...markup.matchAll(/<span data-key="([^"]+)">@Localizer\[("(?:\\.|[^"\\])*")\](?:\.Value)?<\/span>/g)]
        .map(match => [match[1], JSON.parse(match[2])]));
    assert(supplied.size > 0);
    assert.match(markup, /<div id="loc-data" hidden>/);
    for (const name of fs.readdirSync(path.join(root, 'wwwroot/scripts'))) {
        if (!name.endsWith('.js') || name === 'localization.js') continue;
        const source = fs.readFileSync(path.join(root, 'wwwroot/scripts', name), 'utf8');
        for (const call of source.matchAll(/getLocalizedText\("([^"]+)", ("(?:\\.|[^"\\])*")\)/g)) {
            assert.equal(supplied.get(call[1]), JSON.parse(call[2]), `${name}: ${call[1]}`);
        }
    }
});

for (const name of ['dashboard.js']) {
    test(`${name} copies content and shows localized feedback from hidden HTML`, async () => {
        let click;
        let copied;
        const button = {
            innerHTML: '<svg></svg>Copy',
            addEventListener: (_event, handler) => { click = handler; },
            appendChild(child) { this.label = child.textContent; }
        };
        const content = { innerText: 'literal $& {0}', textContent: 'literal $& {0}' };
        const document = {
            getElementById: id => ['copy-dashboard-curl', 'copy-content-btn'].includes(id) ? button :
                id === 'dashboard-curl-code' ? content : null,
            querySelector: selector => selector.startsWith('#loc-data ') ? { textContent: '<已复制> $&' } : content,
            createTextNode: textContent => ({ textContent })
        };
        const timers = [];
        vm.runInNewContext(localization + '\n' + fs.readFileSync(path.join(root, 'wwwroot/scripts', name), 'utf8'), {
            document, window: {}, navigator: { clipboard: { writeText: async text => { copied = text; } } },
            setTimeout: callback => timers.push(callback), setInterval() {}
        });
        await click.call(button);
        assert.equal(copied, 'literal $& {0}');
        assert.equal(button.label, '<已复制> $&');
        assert.equal(timers.length, 1);
    });
}
