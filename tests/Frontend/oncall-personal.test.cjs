const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const test = require('node:test');

const script = fs.readFileSync(path.resolve(__dirname, '../../src/Aiursoft.AlertCenter/wwwroot/scripts/oncall-personal.js'), 'utf8');

function setup({ teamZone = 'UTC', browserZone = 'UTC', instant = '2026-11-09T00:00:00Z', clipboard } = {}) {
    const time = { dateTime: instant, dataset: { zone: teamZone }, appended: [], append(value) { this.appended.push(value); } };
    const countdown = { dataset: { countdown: '2026-11-10T02:05:00Z', label: 'Starts in' } };
    let click;
    let selected = false;
    let interval;
    const button = { dataset: { copied: 'Copied', copyFailed: 'Copy manually' }, addEventListener(_, handler) { click = handler; } };
    const input = { value: 'https://example.com/OnCall/Calendar.ics?token=secret', select() { selected = true; } };
    const status = {};
    class FixedDate extends Date { static now() { return Date.parse('2026-11-09T00:00:00Z'); } }
    const international = {
        DateTimeFormat: function (_, options = {}) { return new Intl.DateTimeFormat('en-US', { ...options, timeZone: options.timeZone || browserZone }); },
        NumberFormat: Intl.NumberFormat
    };
    vm.runInNewContext(script, {
        Intl: international, Date: FixedDate,
        document: {
            querySelectorAll: selector => selector === '[data-oncall-time]' ? [time] : [countdown],
            querySelector: selector => selector === '[data-countdown]' ? countdown : button,
            getElementById: id => id === 'oncall-subscription' ? input : status
        },
        window: { setInterval(handler, milliseconds) { assert.equal(milliseconds, 30000); interval = handler; } },
        navigator: clipboard ? { clipboard } : {}
    });
    return { time, countdown, click, status, input, interval, selected: () => selected };
}

test('team and browser times appear once when equal, including equivalent zone names', () => {
    assert.equal(setup().time.appended.length, 0);
    assert.equal(setup({ teamZone: 'Etc/UTC' }).time.appended.length, 0);
    assert.equal(setup({ teamZone: 'Asia/Shanghai', browserZone: 'Asia/Singapore' }).time.appended.length, 0);
});

test('different browser zones show local times and offsets across DST', () => {
    const winter = setup({ teamZone: 'UTC', browserZone: 'America/New_York' });
    assert.match(winter.time.appended[0], /GMT-5.*America\/New_York/);
    const summer = setup({ teamZone: 'UTC', browserZone: 'America/New_York', instant: '2026-07-09T00:00:00Z' });
    assert.match(summer.time.appended[0], /GMT-4/);
});

test('countdown works without DurationFormat and never displays a negative duration', () => {
    const ui = setup();
    assert.match(ui.countdown.textContent, /Starts in: 1 day 2 hr 5 min/);
    ui.countdown.dataset.countdown = '2020-01-01T00:00:00Z';
    ui.interval();
    assert.match(ui.countdown.textContent, /0 days 0 hr 0 min/);
});

test('copy confirms only after clipboard succeeds and falls back to selecting the secret URL', async () => {
    let copied;
    const ui = setup({ clipboard: { async writeText(value) { copied = value; } } });
    await ui.click();
    assert.equal(copied, ui.input.value);
    assert.equal(ui.status.textContent, 'Copied');
    const denied = setup({ clipboard: { async writeText() { throw new Error('denied'); } } });
    await denied.click();
    assert.equal(denied.status.textContent, 'Copy manually');
    assert.equal(denied.selected(), true);
    const unavailable = setup();
    await unavailable.click();
    assert.equal(unavailable.status.textContent, 'Copy manually');
});
