(() => {
function insertPlaceholder(tag) {
        const textarea = document.getElementById('webhook-payload-template');
        if (!textarea) return;
        const start = textarea.selectionStart;
        const end = textarea.selectionEnd;
        const text = textarea.value;
        textarea.value = text.substring(0, start) + tag + text.substring(end);
        textarea.focus();
        textarea.selectionStart = textarea.selectionEnd = start + tag.length;
    }
    document.querySelectorAll('[data-insert-placeholder]').forEach(button => {
        button.addEventListener('click', () => insertPlaceholder(button.dataset.insertPlaceholder));
    });
})();

(() => {
document.addEventListener('DOMContentLoaded', function () {
            const bindingRoleType = document.getElementById('binding-access-role');
            const bindingUserType = document.getElementById('binding-access-user');
            const bindingRoleSelect = document.getElementById('binding-access-role-select');
            const bindingUserSelect = document.getElementById('binding-access-user-select');
            const bindingSubjectLabel = document.getElementById('binding-access-subject-label');
            const bindingList = document.getElementById('binding-access-list');
            const bindingEmpty = document.getElementById('binding-access-empty');
            const bindingCount = document.getElementById('binding-access-count');

            function selectedBindingKind() { return bindingRoleType.checked ? 'role' : 'user'; }
            function updateBindingSubject() {
                const isRole = selectedBindingKind() === 'role';
                bindingRoleSelect.classList.toggle('d-none', !isRole);
                bindingUserSelect.classList.toggle('d-none', isRole);
                bindingSubjectLabel.textContent = isRole ? getLocalizedText("select-user-group", "Select User Group") : getLocalizedText("select-user", "Select User");
            }
            function updateBindingEmptyState() {
                const count = bindingList.children.length;
                bindingEmpty.classList.toggle('d-none', count > 0);
                bindingCount.textContent = count;
            }
            function createBindingIcon(name, iconClass) {
                const icon = document.createElement('i');
                icon.dataset.lucide = name;
                icon.className = `align-middle ${iconClass}`;
                icon.setAttribute('aria-hidden', 'true');
                return icon;
            }

            bindingRoleType.addEventListener('change', updateBindingSubject);
            bindingUserType.addEventListener('change', updateBindingSubject);
            document.getElementById('add-binding-access').addEventListener('click', function () {
                const kind = selectedBindingKind();
                const select = kind === 'role' ? bindingRoleSelect : bindingUserSelect;
                const option = select.options[select.selectedIndex];
                if (!option.value) {
                    select.focus();
                    return;
                }
                const existing = Array.from(bindingList.children).find(item => item.dataset.kind === kind && item.dataset.id === option.value);
                if (existing) existing.remove();

                const item = document.createElement('div');
                item.className = 'list-group-item d-flex align-items-center justify-content-between gap-3 px-0 py-3';
                item.dataset.kind = kind;
                item.dataset.id = option.value;
                const details = document.createElement('div');
                details.className = 'd-flex align-items-center gap-3 min-w-0';
                const iconContainer = document.createElement('div');
                iconContainer.className = `ac-stat-icon ${kind === 'role' ? 'icon-primary' : 'icon-indigo'}`;
                iconContainer.style.width = '40px';
                iconContainer.style.height = '40px';
                iconContainer.appendChild(createBindingIcon(kind === 'role' ? 'users' : 'user', ''));
                const text = document.createElement('div');
                text.className = 'text-truncate';
                const name = document.createElement('strong');
                name.className = 'd-block text-truncate';
                name.textContent = option.textContent;
                const subtitle = document.createElement('small');
                subtitle.className = 'text-muted';
                subtitle.textContent = kind === 'role' ? getLocalizedText("user-group-members-dynamically-inherit-bound-permissions", "User Group · Members dynamically inherit bound permissions") : getLocalizedText("specify-individual-user", "Specify Individual User");
                text.append(name, subtitle);
                details.append(iconContainer, text);
                const controls = document.createElement('div');
                controls.className = 'd-flex align-items-center gap-2';
                const hidden = document.createElement('input');
                hidden.type = 'hidden';
                hidden.name = kind === 'role' ? 'BindingRoleIds' : 'BindingUserIds';
                hidden.value = option.value;
                const remove = document.createElement('button');
                remove.type = 'button';
                remove.className = 'btn btn-sm btn-outline-danger remove-binding-access';
                remove.title = getLocalizedText("remove-permission", "Remove permission");
                remove.setAttribute('aria-label', getLocalizedText("remove-3", "Remove {0}").replace(/\{(\d+)\}/g, (placeholder, index) => [String(option.textContent)][Number(index)] ?? placeholder));
                remove.appendChild(createBindingIcon('x', 'align-middle'));
                controls.append(hidden, remove);
                item.append(details, controls);
                bindingList.appendChild(item);
                select.value = '';
                if (window.lucide) lucide.createIcons({ nodes: [item] });
                updateBindingEmptyState();
            });
            bindingList.addEventListener('click', function (event) {
                const button = event.target.closest('.remove-binding-access');
                if (!button) return;
                button.closest('.list-group-item').remove();
                updateBindingEmptyState();
            });

if (document.getElementById('email-cc-input')) {
            const emailCcInput = document.getElementById('email-cc-input');
            const parseEmailCcButton = document.getElementById('parse-email-cc');
            const addEmailCcInput = document.getElementById('add-email-cc');
            const addEmailCcButton = document.getElementById('add-email-cc-button');
            const emailCcList = document.getElementById('email-cc-list');
            const emailCcEmpty = document.getElementById('email-cc-empty');
            const emailCcCount = document.getElementById('email-cc-count');

            function normalizeEmail(address) {
                const trimmed = address.trim();
                if (!trimmed || trimmed.length > 320 || /[\r\n,;]/.test(trimmed)) return null;
                const input = document.createElement('input');
                input.type = 'email';
                input.value = trimmed;
                return input.checkValidity() ? trimmed : null;
            }

            function ccAddresses() {
                return emailCcInput.value.split(/[;,\r\n]+/).map(address => address.trim()).filter(Boolean);
            }

            function renderCcAddresses(addresses) {
                const uniqueAddresses = [...new Map(addresses.map(address => [address.toLowerCase(), address])).values()];
                emailCcInput.value = uniqueAddresses.join('\n');
                emailCcList.replaceChildren();
                uniqueAddresses.forEach(function (address) {
                    const badge = document.createElement('span');
                    badge.className = 'badge text-bg-light border d-inline-flex align-items-center gap-1 py-2 ps-2';
                    const text = document.createElement('span');
                    text.className = 'font-monospace fw-normal';
                    text.textContent = address;
                    const remove = document.createElement('button');
                    remove.type = 'button';
                    remove.className = 'btn-close btn-close-sm';
                    remove.setAttribute('aria-label', getLocalizedText("remove-3", "Remove {0}").replace(/\{(\d+)\}/g, (placeholder, index) => [String(address)][Number(index)] ?? placeholder));
                    remove.addEventListener('click', function () {
                        renderCcAddresses(ccAddresses().filter(item => item.toLowerCase() !== address.toLowerCase()));
                    });
                    badge.append(text, remove);
                    emailCcList.appendChild(badge);
                });
                emailCcEmpty.classList.toggle('d-none', uniqueAddresses.length > 0);
                emailCcCount.textContent = uniqueAddresses.length;
            }

            function parseCcAddresses() {
                const addresses = ccAddresses();
                if (addresses.some(address => !normalizeEmail(address))) {
                    emailCcInput.setCustomValidity(getLocalizedText("please-check-the-cc-email-format", "Please check the CC email format."));
                    emailCcInput.reportValidity();
                    return;
                }
                emailCcInput.setCustomValidity('');
                renderCcAddresses(addresses);
            }

            parseEmailCcButton.addEventListener('click', parseCcAddresses);
            addEmailCcButton.addEventListener('click', function () {
                const address = normalizeEmail(addEmailCcInput.value);
                if (!address) {
                    addEmailCcInput.focus();
                    return;
                }
                renderCcAddresses([...ccAddresses(), address]);
                addEmailCcInput.value = '';
            });
            addEmailCcInput.addEventListener('keydown', function (event) {
                if (event.key === 'Enter') {
                    event.preventDefault();
                    addEmailCcButton.click();
                }
            });
            emailCcInput.addEventListener('blur', parseCcAddresses);
            parseCcAddresses();
}

if (document.getElementById('recipient-user-search')) {
            const roleType = document.getElementById('recipient-type-role');
            const userType = document.getElementById('recipient-type-user');
            const roleSelection = document.getElementById('recipient-role-selection');
            const userSelection = document.getElementById('recipient-user-selection');
            const roleSelect = document.getElementById('recipient-role-select');
            const addRoleButton = document.getElementById('add-role-recipient');
            const userSearch = document.getElementById('recipient-user-search');
            const addUserButton = document.getElementById('add-user-recipient');
            const userResults = document.getElementById('recipient-user-results');
            const selectedUserPreview = document.getElementById('selected-user-preview');
            const selectedUserAvatar = document.getElementById('selected-user-avatar');
            const selectedUserName = document.getElementById('selected-user-name');
            const selectedUserUsername = document.getElementById('selected-user-username');
            const selectedUserId = document.getElementById('selected-user-id');
            const recipientList = document.getElementById('recipient-list');
            const recipientsEmpty = document.getElementById('recipients-empty');
            let selectedUser;
            const searchUrl = userSearch.dataset.searchUrl;
            let searchTimeout;
            let searchRequest = 0;

            function updateEmptyState() {
                recipientsEmpty.classList.toggle('d-none', recipientList.children.length > 0);
            }

            function hasRecipient(kind, id) {
                return Array.from(recipientList.querySelectorAll('input')).some(input => input.name === (kind === 'user' ? 'RecipientUserIds' : 'RecipientRoleIds') && input.value === id);
            }

            function createIcon(name, className) {
                const icon = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
                icon.setAttribute('class', 'lucide ' + className);
                icon.setAttribute('width', '18');
                icon.setAttribute('height', '18');
                icon.setAttribute('viewBox', '0 0 24 24');
                icon.setAttribute('fill', 'none');
                icon.setAttribute('stroke', 'currentColor');
                icon.setAttribute('stroke-width', '2');
                icon.setAttribute('stroke-linecap', 'round');
                icon.setAttribute('stroke-linejoin', 'round');
                icon.setAttribute('aria-hidden', 'true');
                const paths = name === 'user'
                    ? ['M19 21v-2a4 4 0 0 0-4-4H9a4 4 0 0 0-4 4v2', 'M12 11a4 4 0 1 0 0-8 4 4 0 0 0 0 8']
                    : name === 'users'
                        ? ['M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2', 'M9 11a4 4 0 1 0 0-8 4 4 0 0 0 0 8', 'M22 21v-2a4 4 0 0 0-3-3.87', 'M16 3.13a4 4 0 0 1 0 7.75']
                        : ['M18 6 6 18', 'm6 6 12 12'];
                paths.forEach(pathData => {
                    const path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
                    path.setAttribute('d', pathData);
                    icon.appendChild(path);
                });
                return icon;
            }

            function addRecipient(kind, id, name, userName) {
                if (!id || hasRecipient(kind, id)) return false;
                const item = document.createElement('div');
                item.className = 'list-group-item d-flex align-items-center justify-content-between gap-3 px-0 py-2';
                item.dataset.recipientKind = kind;

                const details = document.createElement('div');
                details.className = 'd-flex align-items-center gap-2 min-w-0';
                details.appendChild(createIcon(kind === 'user' ? 'user' : 'users', kind === 'user' ? 'align-middle text-info' : 'align-middle text-primary'));
                const text = document.createElement('div');
                text.className = 'text-truncate';
                const title = document.createElement(kind === 'user' ? 'strong' : 'span');
                title.className = kind === 'user' ? 'd-block text-truncate small' : 'fw-bold text-truncate small';
                title.textContent = name;
                text.appendChild(title);
                if (userName) {
                    const subtitle = document.createElement('small');
                    subtitle.className = 'text-muted font-monospace';
                    subtitle.textContent = userName;
                    text.appendChild(subtitle);
                }
                details.appendChild(text);
                item.appendChild(details);

                const hidden = document.createElement('input');
                hidden.type = 'hidden';
                hidden.name = kind === 'user' ? 'RecipientUserIds' : 'RecipientRoleIds';
                hidden.value = id;
                item.appendChild(hidden);
                const remove = document.createElement('button');
                remove.type = 'button';
                remove.className = 'btn btn-sm btn-outline-danger remove-recipient';
                remove.setAttribute('aria-label', getLocalizedText("remove-2", "Remove ") + name);
                remove.appendChild(createIcon('x', 'align-middle'));
                item.appendChild(remove);
                recipientList.appendChild(item);
                updateEmptyState();
                return true;
            }

            function showRoleSelection() {
                roleSelection.classList.remove('d-none');
                userSelection.classList.add('d-none');
                searchRequest++;
                userResults.classList.add('d-none');
            }

            function showUserSelection() {
                userSelection.classList.remove('d-none');
                roleSelection.classList.add('d-none');
                userSearch.focus();
            }

            roleType.addEventListener('change', showRoleSelection);
            userType.addEventListener('change', showUserSelection);
            addRoleButton.addEventListener('click', function () {
                const option = roleSelect.options[roleSelect.selectedIndex];
                if (!option.value) {
                    roleSelect.focus();
                    return;
                }
                if (addRecipient('role', option.value, option.textContent)) roleSelect.value = '';
            });
            addUserButton.addEventListener('click', function () {
                if (!selectedUser) {
                    userSearch.focus();
                    return;
                }
                if (addRecipient('user', selectedUser.id, selectedUser.displayName, selectedUser.userName)) {
                    userSearch.value = '';
                    clearSelectedUser();
                }
            });
            recipientList.addEventListener('click', function (event) {
                const button = event.target.closest('.remove-recipient');
                if (!button) return;
                button.closest('.list-group-item').remove();
                updateEmptyState();
            });

            function clearSelectedUser() {
                selectedUser = undefined;
                addUserButton.disabled = true;
                selectedUserPreview.classList.add('d-none');
            }

            function selectUser(user) {
                selectedUser = user;
                addUserButton.disabled = hasRecipient('user', user.id);
                userSearch.value = '';
                selectedUserAvatar.textContent = Array.from(user.displayName.trim())[0] || '?';
                selectedUserName.textContent = user.displayName;
                selectedUserUsername.textContent = user.userName;
                selectedUserId.textContent = 'ID: ' + user.id;
                selectedUserPreview.classList.remove('d-none');
                clearResults();
            }

            function clearResults() {
                userResults.innerHTML = '';
                userResults.classList.add('d-none');
            }

            function appendUserResult(user) {
                const result = document.createElement('button');
                result.type = 'button';
                result.className = 'list-group-item list-group-item-action d-flex align-items-center gap-3 px-3 py-2 text-start';
                result.setAttribute('role', 'option');
                result.setAttribute('aria-label', getLocalizedText("select-user-2", "Select User ") + user.displayName);

                const avatar = document.createElement('span');
                avatar.className = 'd-inline-flex align-items-center justify-content-center flex-shrink-0 rounded-circle bg-primary-subtle text-primary fw-semibold';
                avatar.style.width = '36px';
                avatar.style.height = '36px';
                avatar.textContent = Array.from(user.displayName.trim())[0] || '?';

                const details = document.createElement('span');
                details.className = 'flex-grow-1 min-w-0';
                const name = document.createElement('strong');
                name.className = 'd-block text-truncate small';
                name.textContent = user.displayName;
                const username = document.createElement('small');
                username.className = 'd-block text-muted text-truncate font-monospace';
                username.textContent = user.userName;
                details.append(name, username);

                const select = document.createElement('span');
                select.className = 'badge bg-primary-subtle text-primary border border-primary-subtle flex-shrink-0';
                select.textContent = getLocalizedText("select", "Select");
                result.append(avatar, details, select);
                result.addEventListener('click', function () {
                    selectUser(user);
                });
                userResults.appendChild(result);
            }

            userSearch.addEventListener('input', function () {
                clearSelectedUser();
                const query = userSearch.value.trim();
                clearTimeout(searchTimeout);
                if (!query) {
                    clearResults();
                    return;
                }
                searchTimeout = setTimeout(async function () {
                    const request = ++searchRequest;
                    try {
                        const response = await fetch(searchUrl + '?query=' + encodeURIComponent(query));
                        if (!response.ok || request !== searchRequest) return;
                        const data = await response.json();
                        userResults.innerHTML = '';
                        if (data.users.length === 0) {
                            const empty = document.createElement('div');
                            empty.className = 'p-3 text-muted small text-center';
                            empty.textContent = getLocalizedText("no-matching-users-found", "No matching users found.");
                            userResults.appendChild(empty);
                        } else {
                            data.users.forEach(appendUserResult);
                        }
                        userResults.classList.remove('d-none');
                    } catch {
                        if (request === searchRequest) clearResults();
                    }
                }, 250);
            });
            userSearch.addEventListener('keydown', function (event) {
                if (event.key === 'Enter') event.preventDefault();
            });
            document.addEventListener('click', function (event) {
                if (!userSearch.contains(event.target) && !userResults.contains(event.target)) clearResults();
            });
}
            });
})();
