(() => {
    const form = document.getElementById('oncall-template-form');
    if (!form) return;
    const mode = document.getElementById('RuleInput_SplitMode');
    const points = document.getElementById('oncall-handover-points');
    function update() {
        const weekly = mode.value === 'weekly';
        document.getElementById('oncall-fixed-duration').hidden = weekly;
        document.getElementById('RuleInput_ShiftMinutes').disabled = weekly;
        document.getElementById('oncall-weekly-points').hidden = !weekly;
        [...points.children].forEach((row, index) => {
            row.querySelector('[data-point-day]').name = `RuleInput.HandoverPoints[${index}].DayOfWeek`;
            let minute = row.querySelector('[type="hidden"]');
            if (!minute) { minute = document.createElement('input'); minute.type = 'hidden'; row.append(minute); }
            minute.name = `RuleInput.HandoverPoints[${index}].MinuteOfDay`;
            const [hours, minutes] = row.querySelector('[data-point-time]').value.split(':').map(Number);
            minute.value = hours * 60 + minutes;
            row.querySelectorAll('input, select').forEach(input => input.disabled = !weekly);
        });
    }
    points.addEventListener('click', event => {
        const button = event.target.closest('[data-remove-point]');
        if (button) { button.closest('[data-handover-point]').remove(); update(); }
    });
    points.addEventListener('input', update);
    document.getElementById('oncall-add-point').addEventListener('click', () => {
        points.append(document.getElementById('oncall-point-template').content.cloneNode(true)); update();
    });
    document.getElementById('oncall-eight-hour-preset').addEventListener('click', () => {
        mode.value = 'fixed'; document.getElementById('RuleInput_ShiftMinutes').value = 480;
        document.getElementById('RuleInput_StaffPerShift').value = 1;
        document.getElementById('RuleInput_SwapAlternateRounds').value = 'false'; update();
    });
    mode.addEventListener('change', update);
    form.addEventListener('submit', update);
    update();
})();
