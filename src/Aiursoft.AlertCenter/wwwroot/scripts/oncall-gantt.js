(() => {
    const chart = document.getElementById('oncall-gantt');
    const detail = document.getElementById('oncall-shift-detail');
    if (!chart || !detail) return;

    function showShift(event) {
        const shift = event.target.closest('.ac-gantt-shift');
        if (!shift) return;
        detail.textContent = shift.getAttribute('aria-label');
    }

    chart.addEventListener('pointerover', showShift);
    chart.addEventListener('focusin', showShift);
    chart.addEventListener('click', showShift);
})();
