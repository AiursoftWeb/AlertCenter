const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const test = require('node:test');
const script = fs.readFileSync(path.resolve(__dirname, '../../src/Aiursoft.AlertCenter/wwwroot/scripts/oncall-group-delete.js'), 'utf8');

test('team deletion requires an exact case-sensitive name without trimming', () => {
    let onInput;
    const input = { value: '', dataset: { teamName: 'Team <A>', mismatch: 'Mismatch' },
        setCustomValidity(message) { this.error = message; }, addEventListener(name, handler) { onInput = handler; } };
    vm.runInNewContext(script, { document: { getElementById: () => input } });
    assert.equal(input.error, 'Mismatch');
    for (const value of ['team <A>', 'Team <A> ', 'Team <A>']) {
        input.value = value;
        onInput();
        assert.equal(input.error, value === input.dataset.teamName ? '' : 'Mismatch');
    }
});
