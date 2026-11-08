(() => {
    const form = document.getElementById('oncall-group-form');
    if (!form) return;
    const selected = document.getElementById('oncall-selected');
    const fallbackList = document.getElementById('oncall-fallback-selected');
    const announcement = document.getElementById('oncall-reorder-status');
    const readUsers = list => new Map([...list.children].map(item => [item.dataset.userId, {
        id: item.dataset.userId, name: item.dataset.name, username: item.dataset.username
    }]));
    const selectedUsers = readUsers(selected), fallbackUsers = readUsers(fallbackList);
    const originalMembers = new Set([...form.querySelectorAll('[data-original-member-id]')].map(element => element.dataset.originalMemberId));
    const format = (text, ...values) => text.replace(/\{(\d+)\}/g, (match, index) => values[index] ?? match);
    const userLabel = user => user.username ? `${user.name} (${user.username})` : user.name;
    const button = (text, className = 'btn btn-sm btn-outline-secondary') => {
        const element = document.createElement('button');
        element.type = 'button'; element.className = className; element.textContent = text;
        return element;
    };
    function membersChanged() {
        form.dispatchEvent(new CustomEvent('oncall-members-changed', { detail: { users: [...selectedUsers.values()] } }));
    }
    function focusMember(id, action = 'handle') {
        const row = [...selected.children].find(item => item.dataset.userId === id);
        const control = row?.querySelector(`[data-member-action="${action}"]`);
        (control && !control.disabled ? control : row?.querySelector('[data-reorder-handle]'))?.focus();
    }
    function moveMember(id, targetIndex, action = 'handle') {
        const users = [...selectedUsers.values()], index = users.findIndex(user => user.id === id);
        if (index < 0 || targetIndex < 0 || targetIndex >= users.length || targetIndex === index) return;
        const [user] = users.splice(index, 1);
        users.splice(targetIndex, 0, user);
        selectedUsers.clear();
        for (const member of users) selectedUsers.set(member.id, member);
        renderSelected();
        focusMember(id, action);
        announcement.textContent = format(getLocalizedText('oncall-member-moved', '{0} moved to position {1} of {2}.'), user.name, targetIndex + 1, users.length);
        membersChanged();
    }
    function renderSelected() {
        cancelDrag();
        selected.replaceChildren();
        document.getElementById('oncall-removal-warning').classList.toggle('d-none', ![...originalMembers].some(id => !selectedUsers.has(id)));
        document.getElementById('oncall-members-empty').hidden = selectedUsers.size > 0;
        const users = [...selectedUsers.values()];
        for (const [index, user] of users.entries()) {
            const item = document.createElement('li');
            item.className = 'list-group-item d-flex flex-wrap gap-2 align-items-center';
            Object.assign(item.dataset, { userId: user.id, name: user.name, username: user.username || '' });
            const input = document.createElement('input');
            input.type = 'hidden'; input.name = 'GroupInput.UserIds'; input.value = user.id; input.className = 'oncall-member-id';
            const handle = button('↕', 'btn btn-sm btn-outline-secondary oncall-reorder-handle');
            handle.dataset.reorderHandle = ''; handle.dataset.memberAction = 'handle';
            handle.setAttribute('aria-label', format(getLocalizedText('oncall-reorder-member', 'Reorder {0}'), user.name));
            handle.setAttribute('aria-describedby', 'oncall-reorder-help');
            const label = document.createElement('span');
            label.className = 'oncall-member-name text-break me-auto'; label.textContent = userLabel(user);
            const actions = document.createElement('div');
            actions.className = 'oncall-member-actions d-flex flex-wrap gap-1';
            for (const [offset, action, text] of [[-1, 'up', getLocalizedText('oncall-move-up', 'Move up')], [1, 'down', getLocalizedText('oncall-move-down', 'Move down')]]) {
                const move = button(text);
                move.dataset.memberAction = action;
                move.disabled = index + offset < 0 || index + offset >= users.length;
                move.addEventListener('click', () => moveMember(user.id, index + offset, action));
                actions.append(move);
            }
            const remove = button(getLocalizedText('remove', 'Remove'), 'btn btn-sm btn-outline-danger oncall-remove');
            remove.dataset.memberAction = 'remove';
            remove.addEventListener('click', () => {
                selectedUsers.delete(user.id); renderSelected(); memberSearch.refresh(); membersChanged();
                const neighbor = [...selectedUsers.keys()][Math.min(index, selectedUsers.size - 1)];
                if (neighbor) focusMember(neighbor, 'remove');
                else document.getElementById('oncall-user-search').focus();
            });
            actions.append(remove); item.append(input, handle, label, actions); selected.append(item);
        }
    }
    function renderFallback() {
        fallbackList.replaceChildren();
        document.getElementById('oncall-fallback-warning').hidden = fallbackUsers.size > 0;
        for (const user of fallbackUsers.values()) {
            const item = document.createElement('li'), input = document.createElement('input'), label = document.createElement('span');
            item.className = 'list-group-item d-flex flex-wrap gap-2 align-items-center';
            item.dataset.userId = user.id;
            input.type = 'hidden'; input.name = 'GroupInput.FallbackUserIds'; input.value = user.id;
            label.className = 'text-break me-auto'; label.textContent = userLabel(user);
            const remove = button(getLocalizedText('remove', 'Remove'), 'btn btn-sm btn-outline-danger');
            remove.dataset.removeFallback = '';
            remove.addEventListener('click', () => {
                fallbackUsers.delete(user.id); renderFallback(); fallbackSearch.refresh();
                document.getElementById('oncall-fallback-search').focus();
            });
            item.append(input, label, remove); fallbackList.append(item);
        }
    }
    function searchPanel(prefix, queryId, selection, onChange, role) {
        const search = document.getElementById(queryId), results = document.getElementById(`${prefix}-search-results`);
        const status = document.getElementById(`${prefix}-search-status`), pageLabel = document.getElementById(`${prefix}-page`);
        const prev = document.getElementById(`${prefix}-prev`), next = document.getElementById(`${prefix}-next`);
        const pagination = document.getElementById(`${prefix}-search-pagination`);
        let page = 1, hasMore = false, sequence = 0, currentResults = [], loaded = false;
        function refresh() {
            results.replaceChildren();
            for (const user of currentResults) {
                const label = document.createElement('label'), checkbox = document.createElement('input'), text = document.createElement('span');
                label.className = 'list-group-item d-flex flex-wrap align-items-center gap-2';
                checkbox.type = 'checkbox'; checkbox.className = 'form-check-input m-0'; checkbox.dataset.userId = user.id;
                checkbox.checked = checkbox.disabled = selection.has(user.id);
                checkbox.addEventListener('change', () => {
                    if (!checkbox.checked || selection.has(user.id)) return;
                    if (selection.size >= 200) {
                        checkbox.checked = false;
                        status.textContent = getLocalizedText('select-up-to-200-employees-per-team', 'Select up to 200 employees per team.');
                        return;
                    }
                    selection.set(user.id, user); onChange(); refresh();
                    const nextChoice = [...results.querySelectorAll('input')].find(input => !input.disabled);
                    (nextChoice || search).focus();
                });
                text.className = 'text-break'; text.textContent = userLabel(user);
                label.append(checkbox, text);
                if (selection.has(user.id)) {
                    const badge = document.createElement('span'); badge.className = 'badge bg-secondary ms-auto';
                    badge.textContent = getLocalizedText('oncall-already-selected', 'Already selected'); label.append(badge);
                }
                results.append(label);
            }
            if (loaded) status.textContent = currentResults.length
                ? format(getLocalizedText('selected-users-maximum-20-per-page', 'Selected {0} users; maximum 20 per page.'), selection.size)
                : getLocalizedText('no-matching-employees', 'No matching employees.');
            pageLabel.textContent = loaded ? format(getLocalizedText('page', 'Page {0}'), page) : '';
            pagination.hidden = page === 1 && !hasMore;
            prev.disabled = !loaded || page === 1; next.disabled = !loaded || !hasMore;
        }
        async function load() {
            const id = ++sequence;
            currentResults = []; loaded = false; results.replaceChildren();
            prev.disabled = next.disabled = true;
            status.textContent = getLocalizedText('searching-for-employees', 'Searching for employees…');
            try {
                const url = new URL(form.dataset.searchUrl, window.location.origin);
                url.searchParams.set('query', role?.value ? '' : search.value.trim());
                url.searchParams.set('roleId', role?.value || ''); url.searchParams.set('page', page);
                const response = await fetch(url, { credentials: 'same-origin' });
                if (!response.ok) throw new Error('search');
                const data = await response.json();
                if (id !== sequence) return;
                currentResults = data.users; hasMore = data.hasMore; loaded = true; refresh();
            } catch {
                if (id !== sequence) return;
                status.textContent = getLocalizedText('failed-to-query-employee-please-try-again', 'Failed to query employee. Please try again.');
            }
        }
        function searchByName() { if (role) role.value = ''; page = 1; load(); }
        document.getElementById(`${prefix}-search-button`).addEventListener('click', searchByName);
        search.addEventListener('keydown', event => {
            if (event.key === 'Enter') { event.preventDefault(); searchByName(); }
        });
        role?.addEventListener('change', () => { search.value = ''; page = 1; load(); });
        prev.addEventListener('click', () => { if (page > 1) { page--; load(); } });
        next.addEventListener('click', () => { if (hasMore) { page++; load(); } });
        return { refresh };
    }

    let drag = null;
    function cancelDrag() {
        if (!drag) return;
        const current = drag;
        drag = null;
        clearTimeout(current.timer);
        if (selected.hasPointerCapture(current.pointerId)) selected.releasePointerCapture(current.pointerId);
        for (const row of selected.children) row.classList.remove('oncall-dragging', 'oncall-drop-target');
    }
    function activateDrag() {
        if (!drag) return;
        drag.active = true;
        drag.row.classList.add('oncall-dragging');
    }
    selected.addEventListener('pointerdown', event => {
        const handle = event.target.closest('[data-reorder-handle]');
        if (!handle || event.button !== 0 || event.isPrimary === false) return;
        cancelDrag();
        const row = handle.closest('[data-user-id]');
        drag = { row, id: row.dataset.userId, targetId: row.dataset.userId, pointerId: event.pointerId,
            x: event.clientX, y: event.clientY, touch: event.pointerType === 'touch', active: false };
        selected.setPointerCapture(event.pointerId);
        if (drag.touch) drag.timer = setTimeout(activateDrag, 450);
    });
    selected.addEventListener('pointermove', event => {
        if (!drag || drag.pointerId !== event.pointerId) return;
        const distance = Math.hypot(event.clientX - drag.x, event.clientY - drag.y);
        if (!drag.active) {
            if (drag.touch) { if (distance > 8) cancelDrag(); return; }
            if (distance < 5) return;
            activateDrag();
        }
        event.preventDefault();
        const target = document.elementFromPoint(event.clientX, event.clientY)?.closest('[data-user-id]');
        if (!target || target.parentElement !== selected) return;
        drag.targetId = target.dataset.userId;
        for (const row of selected.children) row.classList.toggle('oncall-drop-target', row === target && row !== drag.row);
    });
    selected.addEventListener('pointerup', event => {
        if (!drag || drag.pointerId !== event.pointerId) return;
        const { active, id, targetId } = drag;
        cancelDrag();
        if (active) moveMember(id, [...selectedUsers.keys()].indexOf(targetId));
    });
    selected.addEventListener('pointercancel', event => { if (drag?.pointerId === event.pointerId) cancelDrag(); });
    selected.addEventListener('lostpointercapture', event => { if (drag?.pointerId === event.pointerId) cancelDrag(); });
    selected.addEventListener('contextmenu', event => { if (drag?.touch) event.preventDefault(); });
    selected.addEventListener('keydown', event => {
        if (event.key === 'Escape') { cancelDrag(); return; }
        const handle = event.target.closest('[data-reorder-handle]');
        if (!handle || !['ArrowUp', 'ArrowDown'].includes(event.key)) return;
        event.preventDefault();
        const id = handle.closest('[data-user-id]').dataset.userId;
        moveMember(id, [...selectedUsers.keys()].indexOf(id) + (event.key === 'ArrowUp' ? -1 : 1));
    });
    const memberSearch = searchPanel('oncall', 'oncall-user-search', selectedUsers, () => { renderSelected(); membersChanged(); }, document.getElementById('oncall-role'));
    const fallbackSearch = searchPanel('oncall-fallback', 'oncall-fallback-search', fallbackUsers, renderFallback);
    renderSelected(); renderFallback();
})();
