const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const test = require('node:test');
const script = fs.readFileSync(require.resolve('../../src/Aiursoft.AlertCenter/wwwroot/scripts/oncall-group-reset.js'), 'utf8');

function setup(unavailable = false) {
    const element = (dataset = {}) => ({ dataset, hidden: true, addEventListener(type, handler) { this[type] = handler; } });
    const clear = element(), regenerate = element({ unavailable: String(unavailable) });
    const confirm = element({ clear: 'Confirm clear', regenerate: 'Confirm regenerate' });
    const includeManual = { ...element(), checked: false };
    const fields = { id: { value: 'team' }, includeManual, __RequestVerificationToken: { value: 'token' } };
    const form = { ...element({ version: 'page', timeZone: 'America/New_York', noRange: 'N/A' }), elements: { namedItem: name => fields[name] } };
    const results = {};
    const summary = { ...element(), querySelector: selector => results[selector] ??= {} };
    const errors = { ...element(), querySelector() { return this; }, replaceChildren(...children) { this.children = children; } };
    const elements = { form, clear, regenerate, confirm, summary, errors };
    const requests = [];
    let reloads = 0;
    const ui = { ...elements, includeManual, requests, reloads: () => reloads };
    vm.runInNewContext(script, {
        URLSearchParams,
        document: { getElementById: id => elements[id.slice('group-reset-'.length)], createElement: () => ({}) },
        window: { location: { reload: () => reloads++ } },
        fetch: async (_, options) => {
            requests.push(Object.fromEntries(options.body));
            return ui.response ?? { ok: true, json: async () => ({
                version: 'preview', planId: 'plan', deletedCount: 2, generatedCount: 1,
                startUtc: options.body.get('regenerate') === 'true' ? '2026-03-08T05:05:00Z' : null
            }) };
        }
    });
    return ui;
}

test('each action previews first and confirmation uses the selected action, version and plan', async () => {
    const ui = setup();
    await ui.confirm.click();
    assert.equal(ui.requests.length, 0);
    await ui.clear.click();
    assert.equal(ui.confirm.textContent, 'Confirm clear');
    ui.includeManual.checked = true;
    ui.includeManual.change();
    assert.equal(ui.confirm.hidden, true);
    await ui.confirm.click();
    await ui.regenerate.click();
    assert.equal(ui.confirm.textContent, 'Confirm regenerate');
    await ui.confirm.click();
    assert.deepEqual(ui.requests, [
        { id: 'team', regenerate: 'false', includeManual: 'false', preview: 'true', version: 'page' },
        { id: 'team', regenerate: 'true', includeManual: 'true', preview: 'true', version: 'page' },
        { id: 'team', regenerate: 'true', includeManual: 'true', preview: 'false', version: 'preview', planId: 'plan', startUtc: '2026-03-08T05:05:00Z' }
    ]);
    assert.equal(ui.reloads(), 1);
});

test('clear confirmation omits the null preview start', async () => {
    const ui = setup();
    await ui.clear.click();
    await ui.confirm.click();
    assert.deepEqual(ui.requests[1],
        { id: 'team', regenerate: 'false', includeManual: 'false', preview: 'false', version: 'preview', planId: 'plan' });
    assert.equal(ui.reloads(), 1);
});

test('preview displays team-local times across a DST change and handles an absent range', async () => {
    const ui = setup();
    const result = name => ui.summary.querySelector(`[data-result="${name}"]`).textContent;
    await ui.clear.click();
    assert.equal(result('startUtc'), 'N/A');
    assert.equal(result('endUtc'), 'N/A');

    ui.response = { ok: true, json: async () => ({
        version: 'preview', planId: 'plan', deletedCount: 2, generatedCount: 1,
        startUtc: '2026-03-08T05:05:00Z', endUtc: '2026-03-08T07:15:00Z'
    }) };
    await ui.regenerate.click();
    assert.equal(result('startUtc'), '2026-03-08 00:05');
    assert.equal(result('endUtc'), '2026-03-08 03:15');
});

test('unavailable regeneration cannot submit, and failed clear preview cannot be confirmed', async () => {
    const ui = setup(true);
    assert.equal(ui.regenerate.disabled, true);
    await ui.regenerate.click();
    assert.equal(ui.requests.length, 0);
    ui.response = { ok: false, json: async () => ({ errors: ['Schedule changed'] }) };
    await ui.clear.click();
    assert.equal(ui.errors.children[0].textContent, 'Schedule changed');
    assert.equal(ui.confirm.disabled, true);
    assert.equal(ui.regenerate.disabled, true);
    assert.equal(ui.reloads(), 0);
});
