(() => {
    const messages = JSON.parse(document.getElementById('alertcenter-localization')?.textContent || '{}');

    const root = document.getElementById('oncall-schedule');
    if (!root) return;
    const original = JSON.parse(root.dataset.slots);
    const weeks = JSON.parse(root.dataset.weeks);
    const departedIds = new Set([...root.querySelectorAll('.oncall-edit-member')].filter(input => input.dataset.departed === 'true').map(input => input.value));
    const weekSlots = (items, week) => items.slice(week * 21, (week + 1) * 21);
    const editableSlot = index => Number.isInteger(index) && index >= 21 && index < 42;
    const slotChanged = index => [...slots[index].userIds].sort().join(',') !== [...original[index].userIds].sort().join(',');
    const weekChanged = week => weekSlots(slots, week).some((_, index) => slotChanged(week * 21 + index));
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
        const index = Number(select.value);
        const editable = editableSlot(index);
        const assigned = new Set(editable ? slots[index].userIds : []);
        members.forEach(input => {
            input.checked = assigned.has(input.value);
            input.disabled = saving || !editable || input.dataset.departed === 'true';
        });
        const changed = slots.filter((_, index) => slotChanged(index)).length;
        dirty = changed > 0;
        const generate = document.getElementById('oncall-generate');
        if (generate) generate.disabled = dirty || saving;
        document.getElementById('oncall-undo-schedule').disabled = !history.length || saving;
        document.getElementById('oncall-reset-schedule').disabled = !dirty || saving;
        document.getElementById('oncall-edit-count').textContent = (messages["Modified {0} shifts{1}{2}"] ?? "Modified {0} shifts{1}{2}").replace(/\{(\d+)\}/g, (placeholder, index) => [String(changed), String(weekChanged(0) ? (messages[" · includes previous week"] ?? " · includes previous week") : ''), String(weekChanged(2) ? (messages[" · includes next week"] ?? " · includes next week") : '')][Number(index)] ?? placeholder);
        if (badge) {
            badge.textContent = dirty ? (messages["Unsaved · Adjustment draft"] ?? "Unsaved · Adjustment draft") : originalBadge;
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
            row.querySelectorAll('.ac-gantt-shift:not([data-readonly])').forEach(shift => shift.disabled = saving);
            const weekButton = row.querySelector('.ac-gantt-week');
            if (weekButton) weekButton.disabled = saving || mode !== 'move' || row.dataset.departed === 'true';
            row.querySelectorAll('.ac-gantt-cell').forEach(cell => {
                const index = Number(cell.dataset.slot);
                const assigned = slots[index].userIds.includes(row.dataset.userId);
                cell.tabIndex = !saving && mode === 'add' && !assigned && row.dataset.departed !== 'true' ? 0 : -1;
                cell.setAttribute('aria-label', (messages["{0} · {1} · {2}"] ?? "{0} · {1} · {2}").replace(/\{(\d+)\}/g, (placeholder, index) => [String(row.dataset.name), String(slots[index].localStart), String(mode === 'add' ? (messages["Add on-call shift"] ?? "Add on-call shift") : (messages["Move selected shift here"] ?? "Move selected shift here"))][Number(index)] ?? placeholder));
            });
        });
    }

    function setMode(value) {
        if (saving) return;
        mode = value;
        clearSelection();
        suppressClick = false;
        const help = document.getElementById('oncall-mode-help');
        if (help) help.textContent = mode === 'add' ? (messages["Add mode: Click the empty space in the engineer's row to add an 8-hour shift; multiple people can share a shift."] ?? "Add mode: Click the empty space in the engineer's row to add an 8-hour shift; multiple people can share a shift.")
            : mode === 'remove' ? (messages["Delete mode: Click a color block to remove this engineer's current shift. This action can be undone and takes effect after saving all changes."] ?? "Delete mode: Click a color block to remove this engineer's current shift. This action can be undone and takes effect after saving all changes.")
            : (messages["Currently in Move/Swap mode. Switch the mode above before adding or removing shifts."] ?? "Currently in Move/Swap mode. Switch the mode above before adding or removing shifts.");
        status.textContent = '';
        syncMode();
    }
    modeButtons.forEach(button => button.addEventListener('click', () => setMode(button.dataset.scheduleMode)));

    function editAssignment(target) {
        if (target.slot === null || target.week) return;
        const assigned = slots[target.slot].userIds;
        const index = assigned.indexOf(target.user);
        if (mode === 'add' && index !== -1) { status.textContent = (messages["This engineer is already assigned to this shift. No need to add them again."] ?? "This engineer is already assigned to this shift. No need to add them again."); return; }
        if (mode === 'remove' && index === -1) { status.textContent = (messages["No shifts to delete in this cell."] ?? "No shifts to delete in this cell."); return; }
        history.push(structuredClone(slots));
        if (mode === 'add') assigned.push(target.user);
        else assigned.splice(index, 1);
        select.value = target.slot;
        changed((messages["{0} shift for {1} · {2}; not saved yet. You can undo this."] ?? "{0} shift for {1} · {2}; not saved yet. You can undo this.").replace(/\{(\d+)\}/g, (placeholder, index) => [String(mode === 'add' ? (messages["Added"] ?? "Added") : (messages["Removed"] ?? "Removed")), String(target.name), String(slots[target.slot].localStart)][Number(index)] ?? placeholder));
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
        const active = index => Math.floor(index / 21) === 1 || weekChanged(Math.floor(index / 21));
        const empty = slots.flatMap((slot, index) => !slot.userIds.length && active(index) ? [index] : []);
        empty.forEach(index => warnings.push((messages["Gap detected at {0}. Please add staff (the week requires a full 21 shifts)."] ?? "Gap detected at {0}. Please add staff (the week requires a full 21 shifts).").replace(/\{(\d+)\}/g, (placeholder, index) => [String(slots[index].localStart)][Number(index)] ?? placeholder)));
        slots.forEach((slot, index) => {
            const minimum = weeks[Math.floor(index / 21)].minimumStaff;
            if (active(index) && slot.userIds.length > 0 && slot.userIds.length < minimum)
                warnings.push((messages["{0} has only {1} staff, below the minimum of {2}. Manual save is still allowed."] ?? "{0} has only {1} staff, below the minimum of {2}. Manual save is still allowed.").replace(/\{(\d+)\}/g, (placeholder, index) => [String(slot.localStart), String(slot.userIds.length), String(minimum)][Number(index)] ?? placeholder));
        });
        rows.forEach(row => {
            const assigned = slots.flatMap((slot, index) => slot.userIds.includes(row.dataset.userId) ? [index] : []);
            const conflicts = new Set();
            assigned.forEach((index, position) => {
                if (!position) return;
                const previous = assigned[position - 1];
                const rest = (index - previous - 1) * 8;
                const requiredRest = Math.max(weeks[Math.floor(index / 21)].restHours, weeks[Math.floor(previous / 21)].restHours);
                if ((active(index) || active(previous)) && rest < requiredRest) {
                    conflicts.add(index);
                    conflicts.add(previous);
                    warnings.push((messages["{0}: Only {1} hours rest between {2} and {3}. Minimum required is {4} hours."] ?? "{0}: Only {1} hours rest between {2} and {3}. Minimum required is {4} hours.").replace(/\{(\d+)\}/g, (placeholder, index) => [String(row.dataset.name), String(rest), String(slots[previous].localStart), String(slots[index].localStart), String(requiredRest)][Number(index)] ?? placeholder));
                }
            });
            const currentCount = assigned.filter(index => index >= 21 && index < 42).length;
            row.querySelector('.ac-gantt-hours').textContent = (messages["{0} shifts / {1} hours"] ?? "{0} shifts / {1} hours").replace(/\{(\d+)\}/g, (placeholder, index) => [String(currentCount), String(currentCount * 8)][Number(index)] ?? placeholder);
            row.querySelectorAll('.ac-gantt-shift:not(.ac-gantt-external)').forEach(shift => shift.remove());
            row.querySelectorAll('.ac-gantt-cell').forEach(cell => cell.classList.toggle('ac-gantt-empty', empty.includes(Number(cell.dataset.slot))));
            assigned.filter(index => index >= 16 && index < 47).forEach(index => {
                const slot = slots[index];
                const edge = index < 21 ? 'previous' : index >= 42 ? 'next' : null;
                const shift = document.createElement('button');
                shift.type = 'button';
                shift.className = edge ? 'ac-gantt-shift' : `ac-gantt-shift ac-oncall-color-${row.dataset.color}`;
                if (edge) {
                    shift.classList.add('ac-gantt-adjacent');
                    shift.dataset.edge = edge;
                    shift.dataset.readonly = 'true';
                    shift.dataset.groupId = root.dataset.groupId;
                    shift.hidden = true;
                }
                shift.classList.toggle('ac-gantt-conflict', conflicts.has(index));
                const startsAt = Date.parse(slot.startsAtUtc);
                shift.classList.toggle('ac-gantt-current', startsAt <= loadedAt && loadedAt < startsAt + 8 * 3600000);
                const visibleStart = Math.max(-36, (index - 21) * 8);
                const visibleEnd = Math.min(204, (index - 20) * 8);
                shift.style.left = `${visibleStart / 168 * 100}%`;
                shift.style.width = `${(visibleEnd - visibleStart) / 168 * 100}%`;
                if (!edge) shift.dataset.slot = index;
                shift.dataset.summary = (messages["{0}{1}{2} · 8 hours · {3}{4}"] ?? "{0}{1}{2} · 8 hours · {3}{4}").replace(/\{(\d+)\}/g, (placeholder, index) => [String(row.dataset.name), String(row.dataset.departed === 'true' ? (messages[" · Departed"] ?? " · Departed") : ''), String(edge ? (messages[" · {0} · Adjacent week (read-only)"] ?? " · {0} · Adjacent week (read-only)").replace(/\{(\d+)\}/g, (placeholder, index) => [String(root.dataset.groupName)][Number(index)] ?? placeholder) : ''), String(root.dataset.zone), String(conflicts.has(index) ? (messages[" · Insufficient rest"] ?? " · Insufficient rest") : '')][Number(index)] ?? placeholder);
                shift.dataset.start = slot.localStart;
                shift.dataset.end = slot.localEnd;
                shift.title = `${shift.dataset.summary} · ${slot.localStart} → ${slot.localEnd}`;
                shift.setAttribute('aria-label', shift.title);
                shift.setAttribute('aria-describedby', 'oncall-shift-detail');
                if (edge) {
                    const team = document.createElement('span');
                    team.className = 'ac-gantt-time';
                    team.textContent = (messages["{0} (Read-only)"] ?? "{0} (Read-only)").replace(/\{(\d+)\}/g, (placeholder, index) => [String(root.dataset.groupName)][Number(index)] ?? placeholder);
                    shift.append(team);
                }
                [[(messages["From"] ?? "From"), slot.localStart], [(messages["End"] ?? "End"), slot.localEnd]].forEach(([label, time], position) => {
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
        document.getElementById('oncall-draft-summary').textContent = (messages["The schedule has {0} unresolved issues (expand to view); you can continue adjusting."] ?? "The schedule has {0} unresolved issues (expand to view); you can continue adjusting.").replace(/\{(\d+)\}/g, (placeholder, index) => [String(warnings.length)][Number(index)] ?? placeholder);
        const stats = document.querySelectorAll('[data-oncall-stat]');
        const current = weekSlots(slots, 1);
        const covered = current.filter(slot => slot.userIds.length).length;
        const values = { shifts: covered, staff: new Set(current.flatMap(slot => slot.userIds)).size, hours: (messages["{0} hours"] ?? "{0} hours").replace(/\{(\d+)\}/g, (placeholder, index) => [String(covered * 8)][Number(index)] ?? placeholder) };
        stats.forEach(stat => stat.textContent = values[stat.dataset.oncallStat]);
        syncMode();
        chart?.dispatchEvent(new Event('oncall-layout'));
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
        const detailShift = chart?.querySelector(`.ac-gantt-shift[data-slot="${select.value}"]`) || chart?.querySelector('.ac-gantt-shift:not([hidden])');
        if (detailShift) detailShift.dispatchEvent(new Event('pointerover', { bubbles: true }));
        else {
            document.getElementById('oncall-shift-summary')?.replaceChildren(document.createTextNode((messages["No assigned shifts."] ?? "No assigned shifts.")));
            for (const id of ['oncall-shift-start', 'oncall-shift-end']) document.getElementById(id)?.replaceChildren();
        }
    }

    select?.addEventListener('change', renderForm);
    members.forEach(input => input.addEventListener('change', () => {
        if (saving || input.dataset.departed === 'true' || !editableSlot(Number(select.value))) { renderForm(); return; }
        history.push(structuredClone(slots));
        const assigned = slots[Number(select.value)].userIds;
        if (input.checked) {
            if (!assigned.includes(input.value)) assigned.push(input.value);
        } else {
            slots[Number(select.value)].userIds = assigned.filter(id => id !== input.value);
        }
        changed((messages["Staffing adjusted and chart updated; not saved yet."] ?? "Staffing adjusted and chart updated; not saved yet."));
    }));
    document.getElementById('oncall-undo-schedule').addEventListener('click', () => {
        if (!history.length || saving) return;
        slots = history.pop();
        changed((messages["Previous step undone."] ?? "Previous step undone."));
    });
    document.getElementById('oncall-reset-schedule')?.addEventListener('click', () => {
        history.push(structuredClone(slots));
        slots = structuredClone(original);
        changed((messages["Restored to the schedule from when the page was opened."] ?? "Restored to the schedule from when the page was opened."));
    });

    function targetAt(element) {
        const row = element?.closest('[data-user-id]');
        if (!row || !chart.contains(row) || row.dataset.departed === 'true' || element.closest('[data-readonly="true"]')) return null;
        const item = element.closest('.ac-gantt-shift, .ac-gantt-cell, .ac-gantt-week');
        if (!item && !element.closest('.ac-gantt-name')) return null;
        if (item?.hidden) return null;
        if (item?.dataset.slot !== undefined && !editableSlot(Number(item.dataset.slot))) return null;
        return { user: row.dataset.userId, name: row.dataset.name, slot: item?.dataset.slot === undefined ? null : Number(item.dataset.slot),
            week: item?.classList.contains('ac-gantt-week'), occupied: item?.classList.contains('ac-gantt-shift'), element: item || row };
    }

    function pick(target) {
        clearSelection();
        selected = target;
        target.element.classList.add('ac-gantt-selected');
        status.textContent = target.week ? (messages["Selected {0}'s full-week schedule. Drag to or click another engineer."] ?? "Selected {0}'s full-week schedule. Drag to or click another engineer.").replace(/\{(\d+)\}/g, (placeholder, index) => [String(target.name)][Number(index)] ?? placeholder)
            : (messages["Selected {0} · {1}. Drag to or click a color block to swap, Space to move; Esc to cancel."] ?? "Selected {0} · {1}. Drag to or click a color block to swap, Space to move; Esc to cancel.").replace(/\{(\d+)\}/g, (placeholder, index) => [String(target.name), String(slots[target.slot].localStart)][Number(index)] ?? placeholder);
        if (!target.week) { select.value = target.slot; renderForm(); }
    }

    function drop(source, target) {
        clearSelection();
        if (!target || (!source.week && (target.slot === null || target.week))) {
            status.textContent = (messages["Move cancelled; schedule unchanged."] ?? "Move cancelled; schedule unchanged.");
            return;
        }
        const draft = structuredClone(slots);
        if (source.week) {
            if (source.user === target.user) { status.textContent = (messages["Swap cancelled."] ?? "Swap cancelled."); return; }
            weekSlots(draft, 1).forEach(slot => slot.userIds = slot.userIds.map(id => id === source.user ? target.user : id === target.user ? source.user : id));
        } else {
            if (source.slot === target.slot && source.user === target.user) { status.textContent = (messages["Move cancelled."] ?? "Move cancelled."); return; }
            const from = draft[source.slot].userIds;
            const to = draft[target.slot].userIds;
            const swap = to.includes(target.user);
            if (swap && (source.slot === target.slot || source.user === target.user)) { status.textContent = (messages["Two locations already have the same arrangement; no need to swap."] ?? "Two locations already have the same arrangement; no need to swap."); return; }
            if (swap && (from.includes(target.user) || to.includes(source.user))) {
                status.textContent = (messages["Unable to swap directly: one person is already in the target shift. Please adjust members of the same shift using \"Add/Remove members by shift\" first."] ?? "Unable to swap directly: one person is already in the target shift. Please adjust members of the same shift using \"Add/Remove members by shift\" first.");
                return;
            }
            from.splice(from.indexOf(source.user), 1);
            if (swap) { to.splice(to.indexOf(target.user), 1); from.push(target.user); to.push(source.user); }
            else to.push(target.user);
        }
        history.push(structuredClone(slots));
        slots = draft;
        changed(source.week ? (messages["Swapped the full-week schedule for {0} and {1}. Not saved yet."] ?? "Swapped the full-week schedule for {0} and {1}. Not saved yet.").replace(/\{(\d+)\}/g, (placeholder, index) => [String(source.name), String(target.name)][Number(index)] ?? placeholder) : (messages["Shift adjusted; you can continue dragging to resolve red warnings, then save all changes."] ?? "Shift adjusted; you can continue dragging to resolve red warnings, then save all changes."));
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
        ghost.textContent = (messages["{0} · {1}"] ?? "{0} · {1}").replace(/\{(\d+)\}/g, (placeholder, index) => [String(pointer.source.name), String(pointer.source.week ? (messages["Swap whole week"] ?? "Swap whole week") : (messages["Move / swap shifts"] ?? "Move / swap shifts"))][Number(index)] ?? placeholder);
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
            ghost.textContent = pointer.source.week ? (messages["Swap full week: {0} ↔ {1}"] ?? "Swap full week: {0} ↔ {1}").replace(/\{(\d+)\}/g, (placeholder, index) => [String(pointer.source.name), String(target.name)][Number(index)] ?? placeholder)
                : (messages["{0}: {1} · {2}"] ?? "{0}: {1} · {2}").replace(/\{(\d+)\}/g, (placeholder, index) => [String(target.occupied ? (messages["Swap"] ?? "Swap") : (messages["Move to"] ?? "Move to")), String(target.name), String(slots[target.slot].localStart)][Number(index)] ?? placeholder);
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
            if (cancel) { clearSelection(); status.textContent = (messages["Move cancelled; schedule unchanged."] ?? "Move cancelled; schedule unchanged."); }
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
        status.textContent = (messages["Selection cancelled."] ?? "Selection cancelled.");
    });
    chart?.addEventListener('oncall-edge-change', () => {
        if (pointer) endPointer({ pointerId: pointer.id }, true);
        clearSelection();
    });

    const copyDialog = document.getElementById('oncall-copy-confirm');
    const copyModal = window.bootstrap?.Modal.getOrCreateInstance(copyDialog);
    function copyNext() {
        if (saving || !weeks[2].available) return;
        if (weekSlots(slots, 1).some(slot => slot.userIds.some(id => departedIds.has(id)))) {
            showMessages(errors, [(messages["This week includes members who have left. Please replace them in this week's draft before copying to the next week."] ?? "This week includes members who have left. Please replace them in this week's draft before copying to the next week.")]);
            return;
        }
        history.push(structuredClone(slots));
        weekSlots(slots, 1).forEach((slot, index) => slots[42 + index].userIds = [...slot.userIds]);
        changed((messages["Copied the current draft for this week to all 21 shifts in the next week. Not saved yet; you can undo. You can expand the next 36 hours for a read-only preview; after saving, switch to the next week to view or edit the full week."] ?? "Copied the current draft for this week to all 21 shifts in the next week. Not saved yet; you can undo. You can expand the next 36 hours for a read-only preview; after saving, switch to the next week to view or edit the full week."));
        const toggle = document.querySelector('[data-oncall-edge="next"]');
        if (toggle?.getAttribute('aria-expanded') === 'false') toggle.click();
    }
    document.getElementById('oncall-copy-next')?.addEventListener('click', () => {
        if (saving) return;
        if (weeks[2].saved || weekChanged(2)) {
            if (copyModal) copyModal.show();
            else showMessages(errors, [(messages["Unable to display replacement confirmation. Please refresh the page and try again. The next week's draft remains unchanged."] ?? "Unable to display replacement confirmation. Please refresh the page and try again. The next week's draft remains unchanged.")]);
        } else copyNext();
    });
    document.getElementById('oncall-copy-apply')?.addEventListener('click', () => {
        copyModal?.hide();
        copyNext();
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
                body: JSON.stringify({ groupId: root.dataset.groupId, version: root.dataset.version, startUtc: root.dataset.start,
                    slots: weekSlots(slots, 1).map(({ startsAtUtc, userIds }) => ({ startsAtUtc, userIds })),
                    adjacentWeeks: generate ? [] : [0, 2].filter(week => weekChanged(week)).map(week => ({
                        startUtc: weeks[week].startUtc,
                        slots: weekSlots(slots, week).map(({ startsAtUtc, userIds }) => ({ startsAtUtc, userIds }))
                    })) })
            });
            if (!response.ok) {
                const result = await response.json();
                throw new Error((result.errors || [(messages["Save failed. Please refresh the page and try again."] ?? "Save failed. Please refresh the page and try again.")]).join('\n'));
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
                root.querySelectorAll('button, input, select').forEach(input => input.disabled = input.dataset.departed === 'true');
                chart?.querySelectorAll('button').forEach(button => button.disabled = false);
                renderForm();
            }
        }
    }
    document.getElementById('oncall-generate')?.addEventListener('click', () => save(true));
    document.getElementById('oncall-save-schedule')?.addEventListener('click', () => save(false));
    window.addEventListener('beforeunload', event => { if (dirty) { event.preventDefault(); event.returnValue = ''; } });
    renderForm();
    renderChart();
})();
