(() => {
    const root = document.getElementById('oncall-schedule');
    if (!root) return;
    const original = JSON.parse(root.dataset.slots);
    let slots = structuredClone(original);
    const errors = document.getElementById('oncall-schedule-errors');
    const select = document.getElementById('oncall-edit-slot');
    const members = [...root.querySelectorAll('.oncall-edit-member')];
    let dirty = false;
    let saving = false;

    function render() {
        if (!select) return;
        const selected = new Set(slots[Number(select.value)].userIds);
        members.forEach(input => input.checked = selected.has(input.value));
        const changed = slots.filter((slot, index) => [...slot.userIds].sort().join(',') !== [...original[index].userIds].sort().join(',')).length;
        dirty = changed > 0;
        const generate = document.getElementById('oncall-generate');
        if (generate) generate.disabled = dirty;
        document.getElementById('oncall-edit-count').textContent = `已修改 ${changed} 个班次`;
    }
    select?.addEventListener('change', render);
    members.forEach(input => input.addEventListener('change', () => {
        slots[Number(select.value)].userIds = members.filter(member => member.checked).map(member => member.value);
        render();
    }));
    document.getElementById('oncall-reset-schedule')?.addEventListener('click', () => {
        slots = structuredClone(original);
        errors.replaceChildren();
        render();
    });
    document.getElementById('oncall-gantt')?.addEventListener('click', event => {
        const shift = event.target.closest('.ac-gantt-shift');
        if (!shift || !select || saving) return;
        select.value = shift.dataset.slot;
        document.getElementById('oncall-editor').open = true;
        render();
        select.focus({ preventScroll: true });
        select.scrollIntoView({ behavior: 'smooth', block: 'center' });
    });
    async function save(generate) {
        if (saving) return;
        saving = true;
        root.querySelectorAll('button, input, select').forEach(input => input.disabled = true);
        errors.replaceChildren();
        try {
            const response = await fetch(generate ? root.dataset.generateUrl : root.dataset.saveUrl, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json', RequestVerificationToken: root.querySelector('input[name="__RequestVerificationToken"]').value },
                body: JSON.stringify({ ruleId: root.dataset.ruleId, version: root.dataset.version, startUtc: root.dataset.start, slots })
            });
            if (!response.ok) {
                const result = await response.json();
                throw new Error((result.errors || ['保存失败，请刷新页面后重试。']).join('\n'));
            }
            dirty = false;
            window.location.reload();
        } catch (error) {
            for (const message of error.message.split('\n')) {
                const item = document.createElement('li');
                item.textContent = message;
                errors.append(item);
            }
        } finally {
            saving = false;
            root.querySelectorAll('button, input, select').forEach(input => input.disabled = false);
            render();
        }
    }
    document.getElementById('oncall-generate')?.addEventListener('click', () => save(true));
    document.getElementById('oncall-save-schedule')?.addEventListener('click', () => save(false));
    window.addEventListener('beforeunload', event => { if (dirty) { event.preventDefault(); event.returnValue = ''; } });
    render();
})();
