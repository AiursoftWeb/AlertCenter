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

    const edges = { previous: false, next: false };
    const toggles = [...document.querySelectorAll('[data-oncall-edge]')];
    function layout() {
        const before = edges.previous ? 36 : 0;
        const hours = 168 + before + (edges.next ? 36 : 0);
        chart.querySelector('.ac-gantt').style.minWidth = `calc(var(--ac-gantt-name-width) + var(--ac-gantt-toggle-width) + ${hours * 160 / 24}px)`;
        chart.querySelectorAll('[style]').forEach(item => {
            if (!item.style.left || !item.style.left.endsWith('%')) return;
            item.dataset.layoutLeft ??= item.style.left.slice(0, -1);
            if (item.style.width?.endsWith('%')) item.dataset.layoutWidth ??= item.style.width.slice(0, -1);
            item.style.left = `${(Number(item.dataset.layoutLeft) * 168 + before * 100) / hours}%`;
            if (item.dataset.layoutWidth) item.style.width = `${Number(item.dataset.layoutWidth) * 168 / hours}%`;
        });
        chart.querySelectorAll('[data-edge="previous"], [data-edge="next"]').forEach(item => item.hidden = !edges[item.dataset.edge]);
        toggles.forEach(button => {
            const edge = button.dataset.oncallEdge;
            button.setAttribute('aria-expanded', String(edges[edge]));
            button.textContent = (edges[edge] ? getLocalizedText("collapse-36-hours", "Collapse {0} 36 hours").replace(/\{(\d+)\}/g, (placeholder, index) => [String(edge === 'previous' ? getLocalizedText("previous", "previous") : getLocalizedText("next", "next"))][Number(index)] ?? placeholder) : getLocalizedText("expand-36-hours", "Expand {0} 36 hours").replace(/\{(\d+)\}/g, (placeholder, index) => [String(edge === 'previous' ? getLocalizedText("previous", "previous") : getLocalizedText("next", "next"))][Number(index)] ?? placeholder));
        });
    }
    toggles.forEach(button => button.addEventListener('click', () => {
        const edge = button.dataset.oncallEdge;
        edges[edge] = !edges[edge];
        chart.dispatchEvent(new Event('oncall-edge-change'));
        layout();
        if (edges[edge]) chart.scrollLeft = edge === 'previous' ? 0 : Math.max(0, chart.scrollWidth - chart.clientWidth);
    }));
    chart.addEventListener('oncall-layout', layout);
    layout();

    chart.addEventListener('pointerover', showShift);
    chart.addEventListener('focusin', showShift);
    chart.addEventListener('click', showShift);
})();
