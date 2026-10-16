(() => {
    const messages = JSON.parse(document.getElementById('alertcenter-localization')?.textContent || '{}');

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

    function renderSelected() {
        selected.replaceChildren();
        for (const user of selectedUsers.values()) {
            const item = document.createElement('li');
            item.className = 'list-group-item d-flex justify-content-between align-items-center';
            item.dataset.userId = user.id;
            const label = document.createElement('span');
            label.textContent = `${user.name} (${user.username || ''})`;
            const remove = document.createElement('button');
            remove.type = 'button';
            remove.className = 'btn btn-sm btn-outline-danger';
            remove.textContent = (messages["Remove"] ?? "Remove");
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
            item.append(input, label, remove);
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
                        status.textContent = (messages["Select up to 200 employees per team."] ?? "Select up to 200 employees per team.");
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
        if (!users.length) status.textContent = (messages["No matching employees."] ?? "No matching employees.");
        pageLabel.textContent = users.length ? (messages["Page {0}"] ?? "Page {0}").replace(/\{(\d+)\}/g, (placeholder, index) => [String(page)][Number(index)] ?? placeholder) : '';
        prev.disabled = page === 1;
        next.disabled = !hasMore;
    }

    async function load() {
        const id = ++sequence;
        results.replaceChildren();
        prev.disabled = true;
        next.disabled = true;
        status.textContent = (messages["Searching for employees…"] ?? "Searching for employees…");
        try {
            const url = new URL(searchUrl, window.location.origin);
            url.searchParams.set('query', role.value ? '' : search.value.trim());
            url.searchParams.set('roleId', role.value);
            url.searchParams.set('page', page);
            const response = await fetch(url, { credentials: 'same-origin' });
            if (!response.ok) throw new Error((messages["Unable to query employee."] ?? "Unable to query employee."));
            const data = await response.json();
            if (id !== sequence) return;
            hasMore = data.hasMore;
            status.textContent = (messages["Selected {0} users; maximum 20 per page."] ?? "Selected {0} users; maximum 20 per page.").replace(/\{(\d+)\}/g, (placeholder, index) => [String(selectedUsers.size)][Number(index)] ?? placeholder);
            renderResults(data.users);
        } catch {
            if (id !== sequence) return;
            status.textContent = (messages["Failed to query employee. Please try again."] ?? "Failed to query employee. Please try again.");
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
