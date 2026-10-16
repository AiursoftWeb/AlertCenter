(() => {
document.addEventListener('DOMContentLoaded', () => {
        const roleType = document.getElementById('access-role');
        const userType = document.getElementById('access-user');
        const roleSelect = document.getElementById('access-role-select');
        const userSelect = document.getElementById('access-user-select');
        const subjectLabel = document.getElementById('access-subject-label');
        const level = document.getElementById('access-level');
        const list = document.getElementById('access-list');
        const empty = document.getElementById('access-empty');
        const count = document.getElementById('access-count');

        function selectedKind() { return roleType.checked ? 'role' : 'user'; }
        function updateSubject() {
            const isRole = selectedKind() === 'role';
            roleSelect.classList.toggle('d-none', !isRole);
            userSelect.classList.toggle('d-none', isRole);
            subjectLabel.textContent = isRole ? getLocalizedText("select-role", "Select Role") : getLocalizedText("select-user", "Select User");
        }
        function updateEmpty() {
            const total = list.children.length;
            empty.classList.toggle('d-none', total > 0);
            count.textContent = total;
        }

        roleType.addEventListener('change', updateSubject);
        userType.addEventListener('change', updateSubject);

        document.getElementById('add-access').addEventListener('click', () => {
            const kind = selectedKind();
            const select = kind === 'role' ? roleSelect : userSelect;
            const option = select.options[select.selectedIndex];
            if (!option.value) {
                select.focus();
                return;
            }

            const existing = list.querySelector(`[data-kind="${kind}"][data-id="${CSS.escape(option.value)}"]`);
            if (existing) {
                existing.querySelector('.remove-access').click();
            }

            const manage = level.value === 'manage';
            const item = document.createElement('div');
            item.className = 'list-group-item d-flex align-items-center justify-content-between gap-3 px-0 py-3';
            item.dataset.kind = kind;
            item.dataset.id = option.value;

            const iconClass = kind === 'role' ? 'icon-primary' : 'icon-indigo';
            const iconName = kind === 'role' ? 'users' : 'user';
            const subtitle = kind === 'role' ? getLocalizedText("role-group-all-members-in-the-group-automatically", "Role group · All members in the group automatically inherit permissions") : getLocalizedText("specify-individual-user", "Specify Individual User");
            const badgeClass = manage ? 'bg-success-subtle text-success border border-success-subtle' : 'bg-secondary-subtle text-secondary border';
            const badgeText = manage ? getLocalizedText("manageable", "Manageable") : getLocalizedText("view-only", "View only");
            const fieldName = manage ? (kind === 'role' ? 'manageRoleIds' : 'manageUserIds') : (kind === 'role' ? 'viewRoleIds' : 'viewUserIds');

            item.innerHTML = `
                <div class="d-flex align-items-center gap-3 min-w-0">
                    <div class="ac-stat-icon ${iconClass}" style="width: 40px; height: 40px;">
                        <i data-lucide="${iconName}" aria-hidden="true"></i>
                    </div>
                    <div class="text-truncate">
                        <strong class="d-block text-truncate"></strong>
                        <small class="text-muted"></small>
                    </div>
                </div>
                <div class="d-flex align-items-center gap-2">
                    <span class="badge ${badgeClass}"></span>
                    <input type="hidden" name="${fieldName}" value="" />
                    <button type="button" class="btn btn-sm btn-outline-danger remove-access" title="">
                        <i class="align-middle" data-lucide="x" aria-hidden="true"></i>
                        <span class="visually-hidden"></span>
                    </button>
                </div>`;

            item.querySelector('small').textContent = subtitle;
            item.querySelector('.badge').textContent = badgeText;
            item.querySelector('input').value = option.value;
            const removeButton = item.querySelector('.remove-access');
            removeButton.title = getLocalizedText("remove", "Remove");
            removeButton.querySelector('span').textContent = getLocalizedText("remove", "Remove");

            item.querySelector('strong').textContent = option.textContent;
            list.appendChild(item);
            select.value = '';

            if (window.lucide) {
                lucide.createIcons({ nodes: [item] });
            }
            updateEmpty();
        });

        list.addEventListener('click', event => {
            const button = event.target.closest('.remove-access');
            if (!button) return;
            button.closest('.list-group-item').remove();
            updateEmpty();
        });
    });
})();
