(() => {
    const chart = document.getElementById('oncall-gantt');
    const summary = document.getElementById('oncall-shift-summary');
    const start = document.getElementById('oncall-shift-start');
    const end = document.getElementById('oncall-shift-end');
    if (!chart || !summary || !start || !end) return;

    function showShift(event) {
        const shift = event.target.closest('.ac-gantt-shift');
        if (!shift) return;
        summary.textContent = shift.dataset.summary;
        start.textContent = shift.dataset.start;
        end.textContent = shift.dataset.end;
    }

    chart.addEventListener('pointerover', showShift);
    chart.addEventListener('focusin', showShift);
    chart.addEventListener('click', showShift);
})();
