(() => {
    const root = document.getElementById('oncall-schedule');
    if (!root) return;
    const original = JSON.parse(root.dataset.slots);
    let slots = structuredClone(original);
    const errors = document.getElementById('oncall-schedule-errors');
    const select = document.getElementById('oncall-edit-slot');
    const members = [...root.querySelectorAll('.oncall-edit-member')];
    const chart = document.getElementById('oncall-gantt');
    const rows = [...(chart?.querySelectorAll('[data-user-id]') || [])];
    const status = document.getElementById('oncall-drag-status');
    const badge = document.getElementById('oncall-save-state');
    const originalBadge = badge?.textContent;
    const originalBadgeClass = badge?.className;
    const loadedAt = Date.now();
    const history = [];
    const modeButtons = [...root.querySelectorAll('[data-schedule-mode]')];
    let mode = 'move';
    let selected = null;
    let pointer = null;
    let suppressClick = false;
    let dirty = false;
    let saving = false;

    function renderForm() {
        if (!select) return;
        const assigned = new Set(slots[Number(select.value)].userIds);
        members.forEach(input => input.checked = assigned.has(input.value));
        const changed = slots.filter((slot, index) => [...slot.userIds].sort().join(',') !== [...original[index].userIds].sort().join(',')).length;
        dirty = changed > 0;
        const generate = document.getElementById('oncall-generate');
        if (generate) generate.disabled = dirty || saving;
        document.getElementById('oncall-undo-schedule').disabled = !history.length || saving;
        document.getElementById('oncall-reset-schedule').disabled = !dirty || saving;
        document.getElementById('oncall-edit-count').textContent = `已修改 ${changed} 个班次`;
        if (badge) {
            badge.textContent = dirty ? '未保存 · 调整草稿' : originalBadge;
            badge.className = dirty ? 'badge bg-warning text-dark' : originalBadgeClass;
        }
        syncMode();
    }

    function syncMode() {
        if (!chart) return;
        chart.dataset.editMode = mode;
        modeButtons.forEach(button => {
            const active = button.dataset.scheduleMode === mode;
            button.classList.toggle('active', active);
            button.setAttribute('aria-pressed', String(active));
        });
        rows.forEach(row => {
            row.querySelector('.ac-gantt-week').disabled = saving || mode !== 'move';
            row.querySelectorAll('.ac-gantt-cell').forEach(cell => {
                const index = Number(cell.dataset.slot);
                const assigned = slots[index].userIds.includes(row.dataset.userId);
                cell.tabIndex = mode === 'add' && !assigned ? 0 : -1;
                cell.setAttribute('aria-label', `${row.dataset.name} · ${slots[index].localStart} · ${mode === 'add' ? '添加值班' : '移动选中班次到此处'}`);
            });
        });
    }

    function setMode(value) {
        if (saving) return;
        mode = value;
        clearSelection();
        suppressClick = false;
        const help = document.getElementById('oncall-mode-help');
        if (help) help.textContent = mode === 'add' ? '添加模式：点击工程师所在行的空格，添加一个 8 小时班次；允许多人同班。'
            : mode === 'remove' ? '删除模式：点击色块，删除该工程师的本次值班；可撤销，统一保存后生效。'
            : '当前为移动 / 交换模式。添加或删除班次时，请先切换上方模式。';
        status.textContent = '';
        syncMode();
    }
    modeButtons.forEach(button => button.addEventListener('click', () => setMode(button.dataset.scheduleMode)));

    function editAssignment(target) {
        if (target.slot === null || target.week) return;
        const assigned = slots[target.slot].userIds;
        const index = assigned.indexOf(target.user);
        if (mode === 'add' && index !== -1) { status.textContent = '该工程师已在此班次，无需重复添加。'; return; }
        if (mode === 'remove' && index === -1) { status.textContent = '此格没有需要删除的班次。'; return; }
        history.push(structuredClone(slots));
        if (mode === 'add') assigned.push(target.user);
        else assigned.splice(index, 1);
        select.value = target.slot;
        changed(`已${mode === 'add' ? '添加' : '删除'} ${target.name} · ${slots[target.slot].localStart} 的值班；尚未保存，可撤销。`);
    }

    function showMessages(container, messages) {
        container.replaceChildren(...messages.map(message => {
            const item = document.createElement('li');
            item.textContent = message;
            return item;
        }));
    }

    function renderChart() {
        const warnings = [];
        const empty = slots.flatMap((slot, index) => slot.userIds.length ? [] : [index]);
        empty.forEach(index => warnings.push(`${slots[index].localStart} 出现空班，请补充人员。`));
        rows.forEach(row => {
            const assigned = slots.flatMap((slot, index) => slot.userIds.includes(row.dataset.userId) ? [index] : []);
            const conflicts = new Set();
            assigned.forEach((index, position) => {
                if (!position) return;
                const previous = assigned[position - 1];
                const rest = (index - previous - 1) * 8;
                if (rest < Number(root.dataset.restHours)) {
                    conflicts.add(index);
                    conflicts.add(previous);
                    warnings.push(`${row.dataset.name}：${slots[previous].localStart} 与 ${slots[index].localStart} 两班之间仅休息 ${rest} 小时，至少需要 ${root.dataset.restHours} 小时。`);
                }
            });
            row.querySelector('.ac-gantt-hours').textContent = `${assigned.length} 班 / ${assigned.length * 8} 小时`;
            row.querySelectorAll('.ac-gantt-shift').forEach(shift => shift.remove());
            row.querySelectorAll('.ac-gantt-cell').forEach(cell => cell.classList.toggle('ac-gantt-empty', empty.includes(Number(cell.dataset.slot))));
            assigned.forEach(index => {
                const slot = slots[index];
                const shift = document.createElement('button');
                shift.type = 'button';
                shift.className = `ac-gantt-shift ac-oncall-color-${row.dataset.color}`;
                shift.classList.toggle('ac-gantt-conflict', conflicts.has(index));
                const startsAt = Date.parse(slot.startsAtUtc);
                shift.classList.toggle('ac-gantt-current', startsAt <= loadedAt && loadedAt < startsAt + 8 * 3600000);
                shift.style.left = `${index / 21 * 100}%`;
                shift.style.width = `${100 / 21}%`;
                shift.dataset.slot = index;
                shift.dataset.summary = `${row.dataset.name} · 8 小时 · ${root.dataset.zone}${conflicts.has(index) ? ' · 休息不足' : ''}`;
                shift.dataset.start = slot.localStart;
                shift.dataset.end = slot.localEnd;
                shift.title = `${shift.dataset.summary} · ${slot.localStart} → ${slot.localEnd}`;
                shift.setAttribute('aria-label', shift.title);
                shift.setAttribute('aria-describedby', 'oncall-shift-detail');
                [['起', slot.localStart], ['止', slot.localEnd]].forEach(([label, time], position) => {
                    const span = document.createElement('span');
                    span.className = `ac-gantt-time${position ? ' ac-gantt-time-end' : ''}`;
                    span.textContent = `${label} ${time.slice(11, 16)}`;
                    span.setAttribute('aria-hidden', 'true');
                    shift.append(span);
                });
                row.querySelector('.ac-gantt-lane').append(shift);
            });
        });
        showMessages(document.getElementById('oncall-draft-errors'), warnings);
        document.getElementById('oncall-draft-validation').hidden = warnings.length === 0;
        document.getElementById('oncall-draft-summary').textContent = `本周有 ${warnings.length} 处待解决问题（展开查看）；可以继续调整。`;
        const stats = document.querySelectorAll('[data-oncall-stat]');
        const values = { shifts: 21 - empty.length, staff: new Set(slots.flatMap(slot => slot.userIds)).size, hours: `${(21 - empty.length) * 8} 小时` };
        stats.forEach(stat => stat.textContent = values[stat.dataset.oncallStat]);
        syncMode();
    }

    function clearSelection() {
        selected = null;
        chart?.querySelectorAll('.ac-gantt-selected').forEach(item => item.classList.remove('ac-gantt-selected'));
    }

    function changed(message) {
        clearSelection();
        errors.replaceChildren();
        status.textContent = message;
        renderForm();
        renderChart();
        const detailShift = chart?.querySelector(`.ac-gantt-shift[data-slot="${select.value}"]`) || chart?.querySelector('.ac-gantt-shift');
        if (detailShift) detailShift.dispatchEvent(new Event('pointerover', { bubbles: true }));
        else {
            document.getElementById('oncall-shift-summary')?.replaceChildren(document.createTextNode('暂无已分配的班次。'));
            for (const id of ['oncall-shift-start', 'oncall-shift-end']) document.getElementById(id)?.replaceChildren();
        }
    }

    select?.addEventListener('change', renderForm);
    members.forEach(input => input.addEventListener('change', () => {
        history.push(structuredClone(slots));
        slots[Number(select.value)].userIds = members.filter(member => member.checked).map(member => member.value);
        changed('人员已调整，图表已更新；尚未保存。');
    }));
    document.getElementById('oncall-undo-schedule').addEventListener('click', () => {
        if (!history.length || saving) return;
        slots = history.pop();
        changed('已撤销上一步。');
    });
    document.getElementById('oncall-reset-schedule')?.addEventListener('click', () => {
        history.push(structuredClone(slots));
        slots = structuredClone(original);
        changed('已还原为打开页面时的排班。');
    });

    function targetAt(element) {
        const row = element?.closest('[data-user-id]');
        if (!row || !chart.contains(row)) return null;
        const item = element.closest('.ac-gantt-shift, .ac-gantt-cell, .ac-gantt-week');
        return { user: row.dataset.userId, name: row.dataset.name, slot: item?.dataset.slot === undefined ? null : Number(item.dataset.slot),
            week: item?.classList.contains('ac-gantt-week'), occupied: item?.classList.contains('ac-gantt-shift'), element: item || row };
    }

    function pick(target) {
        clearSelection();
        selected = target;
        target.element.classList.add('ac-gantt-selected');
        status.textContent = target.week ? `已选中 ${target.name} 的整周安排，请拖到或点击另一位工程师。`
            : `已选中 ${target.name} · ${slots[target.slot].localStart}。拖到或点击色块交换，空格移动；Esc 取消。`;
        if (!target.week) { select.value = target.slot; renderForm(); }
    }

    function drop(source, target) {
        clearSelection();
        if (!target || (!source.week && (target.slot === null || target.week))) {
            status.textContent = '已取消移动，排班未改变。';
            return;
        }
        const draft = structuredClone(slots);
        if (source.week) {
            if (source.user === target.user) { status.textContent = '已取消交换。'; return; }
            draft.forEach(slot => slot.userIds = slot.userIds.map(id => id === source.user ? target.user : id === target.user ? source.user : id));
        } else {
            if (source.slot === target.slot && source.user === target.user) { status.textContent = '已取消移动。'; return; }
            const from = draft[source.slot].userIds;
            const to = draft[target.slot].userIds;
            const swap = to.includes(target.user);
            if (swap && (source.slot === target.slot || source.user === target.user)) { status.textContent = '两处已有相同安排，无需交换。'; return; }
            if (swap && (from.includes(target.user) || to.includes(source.user))) {
                status.textContent = '无法直接交换：其中一人已在目标班次。请先通过「按班次增减人员」调整同班成员。';
                return;
            }
            from.splice(from.indexOf(source.user), 1);
            if (swap) { to.splice(to.indexOf(target.user), 1); from.push(target.user); to.push(source.user); }
            else to.push(target.user);
        }
        history.push(structuredClone(slots));
        slots = draft;
        changed(source.week ? `已交换 ${source.name} 与 ${target.name} 的整周安排；尚未保存。` : '班次已调整；可继续拖动解决红色提示，再统一保存。');
    }

    chart?.classList.add('ac-gantt-editable');
    chart?.addEventListener('click', event => {
        if (saving || (suppressClick && event.detail !== 0)) return;
        const target = targetAt(event.target);
        if (!target) return;
        if (mode !== 'move') editAssignment(target);
        else if (selected) drop(selected, target);
        else if (target.occupied || target.week) pick(target);
    });
    const ghost = document.createElement('div');
    ghost.className = 'ac-gantt-drag-label';
    ghost.hidden = true;
    document.body.append(ghost);
    chart?.addEventListener('pointerdown', event => {
        suppressClick = false;
        if (saving || mode !== 'move' || !event.isPrimary || event.button !== 0) return;
        const source = targetAt(event.target);
        if (!source || (!source.occupied && !source.week)) return;
        pointer = { source, id: event.pointerId, x: event.clientX, y: event.clientY, dragging: false };
        chart.setPointerCapture(event.pointerId);
    });
    chart?.addEventListener('pointermove', event => {
        if (!pointer || event.pointerId !== pointer.id) return;
        if (!pointer.dragging && Math.hypot(event.clientX - pointer.x, event.clientY - pointer.y) < 6) return;
        if (!pointer.dragging) { pointer.dragging = true; pick(pointer.source); }
        chart.classList.add('ac-gantt-dragging');
        ghost.hidden = false;
        ghost.textContent = `${pointer.source.name} · ${pointer.source.week ? '交换整周' : '移动 / 交换班次'}`;
        ghost.style.left = `${Math.min(event.clientX + 12, window.innerWidth - ghost.offsetWidth)}px`;
        ghost.style.top = `${Math.min(event.clientY + 12, window.innerHeight - ghost.offsetHeight)}px`;
        const bounds = chart.getBoundingClientRect();
        const namesEnd = chart.querySelector('.ac-gantt-name').getBoundingClientRect().right;
        if (!pointer.source.week && event.clientX > bounds.right - 12) chart.scrollLeft += 12;
        if (!pointer.source.week && event.clientX < namesEnd + 12) chart.scrollLeft -= 12;
        chart.querySelectorAll('.ac-gantt-drop-target').forEach(item => item.classList.remove('ac-gantt-drop-target'));
        const target = targetAt(document.elementFromPoint(event.clientX, event.clientY));
        if (target && (pointer.source.week || target.slot !== null)) {
            target.element.classList.add('ac-gantt-drop-target');
            ghost.textContent = pointer.source.week ? `交换整周：${pointer.source.name} ↔ ${target.name}`
                : `${target.occupied ? '交换' : '移动到'}：${target.name} · ${slots[target.slot].localStart}`;
        }
    });
    function endPointer(event, cancel = false) {
        if (!pointer || event.pointerId !== pointer.id) return;
        const finished = pointer;
        pointer = null;
        if (chart.hasPointerCapture(finished.id)) chart.releasePointerCapture(finished.id);
        ghost.hidden = true;
        chart.classList.remove('ac-gantt-dragging');
        chart.querySelectorAll('.ac-gantt-drop-target').forEach(item => item.classList.remove('ac-gantt-drop-target'));
        const target = cancel ? null : targetAt(document.elementFromPoint(event.clientX, event.clientY));
        const apply = () => {
            if (cancel) { clearSelection(); status.textContent = '已取消移动，排班未改变。'; }
            else if (finished.dragging) drop(finished.source, target);
            else if (selected) drop(selected, finished.source);
            else pick(finished.source);
        };
        suppressClick = true;
        // 保留触摸目标直到 touchend 分发完毕，避免重绘移除目标后影响后续点击。
        if (event.pointerType === 'touch' && !cancel) requestAnimationFrame(apply);
        else apply();
    }
    chart?.addEventListener('pointerup', event => endPointer(event));
    chart?.addEventListener('pointercancel', event => endPointer(event, true));
    document.addEventListener('keydown', event => {
        if (event.key !== 'Escape') return;
        if (pointer) endPointer({ pointerId: pointer.id }, true);
        setMode('move');
        clearSelection();
        status.textContent = '已取消选择。';
    });
    async function save(generate) {
        if (saving) return;
        saving = true;
        let reloading = false;
        clearSelection();
        chart?.querySelectorAll('button').forEach(button => button.disabled = true);
        root.querySelectorAll('button, input, select').forEach(input => input.disabled = true);
        errors.replaceChildren();
        try {
            const response = await fetch(generate ? root.dataset.generateUrl : root.dataset.saveUrl, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json', RequestVerificationToken: root.querySelector('input[name="__RequestVerificationToken"]').value },
                body: JSON.stringify({ ruleId: root.dataset.ruleId, version: root.dataset.version, startUtc: root.dataset.start,
                    slots: slots.map(({ startsAtUtc, userIds }) => ({ startsAtUtc, userIds })) })
            });
            if (!response.ok) {
                const result = await response.json();
                throw new Error((result.errors || ['保存失败，请刷新页面后重试。']).join('\n'));
            }
            dirty = false;
            reloading = true;
            window.location.reload();
        } catch (error) {
            showMessages(errors, error.message.split('\n'));
            errors.scrollIntoView({ behavior: 'smooth', block: 'center' });
        } finally {
            if (!reloading) {
                saving = false;
                root.querySelectorAll('button, input, select').forEach(input => input.disabled = false);
                chart?.querySelectorAll('button').forEach(button => button.disabled = false);
                renderForm();
            }
        }
    }
    document.getElementById('oncall-generate')?.addEventListener('click', () => save(true));
    document.getElementById('oncall-save-schedule')?.addEventListener('click', () => save(false));
    window.addEventListener('beforeunload', event => { if (dirty) { event.preventDefault(); event.returnValue = ''; } });
    renderForm();
})();
