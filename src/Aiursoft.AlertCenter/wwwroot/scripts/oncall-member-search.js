(() => {

    const form = document.getElementById('oncall-group-form');
    if (!form) return;
    const search = document.getElementById('oncall-user-search');
    const role = document.getElementById('oncall-role');
    const results = document.getElementById('oncall-search-results');
    const selected = document.getElementById('oncall-selected');
    const status = document.getElementById('oncall-search-status');
    const pageLabel = document.getElementById('oncall-page');
    const prev = document.getElementById('oncall-prev');
    const next = document.getElementById('oncall-next');
    const searchUrl = form.dataset.searchUrl;
    let page = 1;
    let hasMore = false;
    let sequence = 0;
    const selectedUsers = new Map();

    for (const item of selected.children) {
        selectedUsers.set(item.dataset.userId, { id: item.dataset.userId, name: item.dataset.name, username: item.dataset.username });
    }

    const originalMembers = new Set([...form.querySelectorAll('[data-original-member-id]')].map(element => element.dataset.originalMemberId));

    function renderSelected() {
        selected.replaceChildren();
        document.getElementById('oncall-removal-warning').classList.toggle('d-none', ![...originalMembers].some(id => !selectedUsers.has(id)));
        document.getElementById('oncall-members-empty').hidden = selectedUsers.size > 0;
        const users = [...selectedUsers.values()];
        for (const [index, user] of users.entries()) {
            const item = document.createElement('li');
            item.className = 'list-group-item d-flex flex-wrap gap-2 justify-content-between align-items-center';
            item.dataset.userId = user.id;
            const label = document.createElement('span');
            label.textContent = `${user.name} (${user.username || ''})`;
            label.className = 'text-break me-auto';
            const remove = document.createElement('button');
            remove.type = 'button';
            remove.className = 'btn btn-sm btn-outline-danger';
            remove.textContent = getLocalizedText("remove", "Remove");
            remove.addEventListener('click', () => {
                selectedUsers.delete(user.id);
                renderSelected();
                renderResults(currentResults);
            });
            const input = document.createElement('input');
            input.type = 'hidden';
            input.name = 'GroupInput.UserIds';
            input.value = user.id;
            input.className = 'oncall-member-id';
            const actions = document.createElement('div');
            actions.className = 'd-flex flex-wrap gap-1';
            for (const [offset, key, fallback] of [[-1, 'oncall-move-up', 'Move up'], [1, 'oncall-move-down', 'Move down']]) {
                const move = document.createElement('button');
                move.type = 'button';
                move.className = 'btn btn-sm btn-outline-secondary';
                move.textContent = getLocalizedText(key, fallback);
                move.disabled = index + offset < 0 || index + offset >= users.length;
                move.addEventListener('click', () => {
                    const target = index + offset;
                    [users[index], users[target]] = [users[target], users[index]];
                    selectedUsers.clear();
                    for (const entry of users) selectedUsers.set(entry.id, entry);
                    renderSelected();
                    selected.children[target].querySelectorAll('button')[offset < 0 ? 0 : 1].focus();
                });
                actions.append(move);
            }
            actions.append(remove);
            item.append(input, label, actions);
            selected.append(item);
        }
    }

    let currentResults = [];
    function renderResults(users) {
        currentResults = users;
        results.replaceChildren();
        for (const user of users) {
            const label = document.createElement('label');
            label.className = 'list-group-item d-flex align-items-center gap-2';
            const checkbox = document.createElement('input');
            checkbox.type = 'checkbox';
            checkbox.className = 'form-check-input m-0';
            checkbox.checked = selectedUsers.has(user.id);
            checkbox.addEventListener('change', () => {
                if (checkbox.checked) {
                    if (selectedUsers.size >= 200) {
                        checkbox.checked = false;
                        status.textContent = getLocalizedText("select-up-to-200-employees-per-team", "Select up to 200 employees per team.");
                        return;
                    }
                    selectedUsers.set(user.id, user);
                } else {
                    selectedUsers.delete(user.id);
                }
                renderSelected();
            });
            const text = document.createElement('span');
            text.textContent = `${user.name} (${user.username || ''})`;
            label.append(checkbox, text);
            results.append(label);
        }
        if (!users.length) status.textContent = getLocalizedText("no-matching-employees", "No matching employees.");
        pageLabel.textContent = users.length ? getLocalizedText("page", "Page {0}").replace(/\{(\d+)\}/g, (placeholder, index) => [String(page)][Number(index)] ?? placeholder) : '';
        document.getElementById('oncall-search-pagination').hidden = page === 1 && !hasMore;
        prev.disabled = page === 1;
        next.disabled = !hasMore;
    }

    async function load() {
        const id = ++sequence;
        results.replaceChildren();
        prev.disabled = true;
        next.disabled = true;
        status.textContent = getLocalizedText("searching-for-employees", "Searching for employees…");
        try {
            const url = new URL(searchUrl, window.location.origin);
            url.searchParams.set('query', role.value ? '' : search.value.trim());
            url.searchParams.set('roleId', role.value);
            url.searchParams.set('page', page);
            const response = await fetch(url, { credentials: 'same-origin' });
            if (!response.ok) throw new Error(getLocalizedText("unable-to-query-employee", "Unable to query employee."));
            const data = await response.json();
            if (id !== sequence) return;
            hasMore = data.hasMore;
            status.textContent = getLocalizedText("selected-users-maximum-20-per-page", "Selected {0} users; maximum 20 per page.").replace(/\{(\d+)\}/g, (placeholder, index) => [String(selectedUsers.size)][Number(index)] ?? placeholder);
            renderResults(data.users);
        } catch {
            if (id !== sequence) return;
            status.textContent = getLocalizedText("failed-to-query-employee-please-try-again", "Failed to query employee. Please try again.");
        }
    }

    function searchByName() {
        role.value = '';
        page = 1;
        load();
    }
    document.getElementById('oncall-search-button').addEventListener('click', searchByName);
    search.addEventListener('keydown', event => {
        if (event.key === 'Enter') { event.preventDefault(); searchByName(); }
    });
    role.addEventListener('change', () => { search.value = ''; page = 1; load(); });
    prev.addEventListener('click', () => { if (page > 1) { page--; load(); } });
    next.addEventListener('click', () => { if (hasMore) { page++; load(); } });
    renderSelected();
})();
