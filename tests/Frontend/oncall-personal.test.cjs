const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const test = require('node:test');

const script = fs.readFileSync(path.resolve(__dirname, '../../src/Aiursoft.AlertCenter/wwwroot/scripts/oncall-personal.js'), 'utf8');
const { localDateKey, splitShiftByLocalDate, monthGrid, monthBounds, navigateMonth, collapseDay } = require('../../src/Aiursoft.AlertCenter/wwwroot/scripts/oncall-personal-calendar.js');

function inTimeZone(zone, run) {
    const previous = process.env.TZ;
    process.env.TZ = zone;
    try { run(); }
    finally {
        if (previous === undefined) delete process.env.TZ;
        else process.env.TZ = previous;
    }
}

const shiftBetween = (startsAtUtc, endsAtUtc) => ({ startsAtUtc, endsAtUtc });

test('calendar splits by browser dates, not UTC or team dates, and excludes an end at midnight', () => {
    inTimeZone('Asia/Shanghai', () => {
        const shift = shiftBetween('2026-11-09T15:00:00Z', '2026-11-10T16:00:00Z');
        const segments = splitShiftByLocalDate(shift);
        assert.deepEqual(segments.map(segment => segment.date), ['2026-11-09', '2026-11-10']);
        assert.deepEqual(segments.map(({ startsHere, endsHere }) => [startsHere, endsHere]), [[true, false], [false, true]]);
        assert.equal(segments[0].start.getHours(), 23);
        assert.equal(segments[1].end.getHours(), 0);
        assert.equal(segments[1].shift, shift);
        assert.equal(splitShiftByLocalDate(shiftBetween('2026-11-09T02:00:00Z', '2026-11-09T03:00:00Z')).length, 1);
    });
});

test('multi-day shifts keep full intermediate days and can be clipped to a visible month', () => {
    inTimeZone('UTC', () => {
        const shift = shiftBetween('2026-10-30T23:00:00Z', '2026-11-03T01:00:00Z');
        const segments = splitShiftByLocalDate(shift);
        assert.equal(segments.length, 5);
        assert.deepEqual(segments.slice(1, -1).map(({ startsHere, endsHere }) => [startsHere, endsHere]), [[false, false], [false, false], [false, false]]);
        assert.deepEqual(splitShiftByLocalDate(shift, new Date('2026-11-01Z'), new Date('2026-11-03Z')).map(segment => segment.date), ['2026-11-01', '2026-11-02']);
        assert.deepEqual(splitShiftByLocalDate(shift, new Date('2026-12-01Z'), new Date('2027-01-01Z')), []);
        assert.deepEqual(splitShiftByLocalDate(shiftBetween(shift.endsAtUtc, shift.startsAtUtc)), []);
    });
});

test('local day splitting handles 23-hour spring and 25-hour fall DST days without skipping or duplicating dates', () => {
    inTimeZone('America/New_York', () => {
        for (const [start, end, dates, hours] of [
            ['2026-03-07T23:00:00-05:00', '2026-03-09T01:00:00-04:00', ['2026-03-07', '2026-03-08', '2026-03-09'], [1, 23, 1]],
            ['2026-10-31T23:00:00-04:00', '2026-11-02T01:00:00-05:00', ['2026-10-31', '2026-11-01', '2026-11-02'], [1, 25, 1]]
        ]) {
            const segments = splitShiftByLocalDate(shiftBetween(start, end));
            assert.deepEqual(segments.map(segment => segment.date), dates);
            assert.deepEqual(segments.map(segment => (segment.end - segment.start) / 3600000), hours);
            assert.deepEqual(segments.map(({ startsHere, endsHere }) => [startsHere, endsHere]), [[true, false], [false, false], [false, true]]);
        }
        const repeatedHour = splitShiftByLocalDate(shiftBetween('2026-11-01T01:30:00-04:00', '2026-11-01T01:30:00-05:00'));
        assert.equal(repeatedHour.length, 1);
        assert.equal(repeatedHour[0].end - repeatedHour[0].start, 3600000);
    });
});

test('month grids start on Monday and include adjacent dates across years and DST', () => {
    inTimeZone('America/New_York', () => {
        for (const [month, first, last, count] of [
            [new Date(2027, 0, 15), '2026-12-28', '2027-01-31', 31],
            [new Date(2026, 2, 1), '2026-02-23', '2026-04-05', 31],
            [new Date(2028, 1, 1), '2028-01-31', '2028-03-05', 29]
        ]) {
            const grid = monthGrid(month);
            assert.equal(grid[0].key, first);
            assert.equal(grid.at(-1).key, last);
            assert.equal(grid[0].date.getDay(), 1);
            assert.equal(grid.length % 7, 0);
            assert.equal(new Set(grid.map(cell => cell.key)).size, grid.length);
            assert.equal(grid.filter(cell => cell.inMonth).length, count);
        }
    });
});

test('navigation clamps to the current through eight-weeks-later months, even across years', () => {
    inTimeZone('America/New_York', () => {
        const bounds = monthBounds(new Date('2026-12-20T12:00:00-05:00'));
        assert.equal(localDateKey(bounds.first), '2026-12-01');
        assert.equal(localDateKey(bounds.last), '2027-02-01');
        assert.equal(localDateKey(navigateMonth(bounds.first, -1, bounds)), '2026-12-01');
        assert.equal(localDateKey(navigateMonth(bounds.first, 1, bounds)), '2027-01-01');
        assert.equal(localDateKey(navigateMonth(bounds.last, 1, bounds)), '2027-02-01');
        assert.equal(localDateKey(navigateMonth(bounds.last, -1, bounds)), '2027-01-01');
    });
});

test('day overflow shows at most three shifts without losing the full ordered list', () => {
    const all = Array.from({ length: 8 }, (_, id) => ({ id }));
    for (const count of [0, 1, 3, 4, 8]) {
        const segments = all.slice(0, count);
        const result = collapseDay(segments);
        assert.deepEqual(result.visible, segments.slice(0, 3));
        assert.equal(result.hiddenCount, Math.max(0, count - 3));
        assert.equal(segments.length, count);
    }
});

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
