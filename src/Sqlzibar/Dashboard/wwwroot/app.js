(function() {
    const basePath = window.location.pathname.replace(/\/$/, '');
    const api = (endpoint) => fetch(`${basePath}/api/${endpoint}`).then(r => r.json());
    const apiPost = (endpoint, body) => fetch(`${basePath}/api/${endpoint}`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body)
    });
    const apiDelete = (endpoint) => fetch(`${basePath}/api/${endpoint}`, { method: 'DELETE' });
    const $ = (sel) => document.querySelector(sel);
    const content = $('#content');
    let currentView = 'resources';
    let subjectDetailContext = null;  // { subjectId, backView } when on subject detail
    let resourceDetailContext = null; // resourceId when on resource detail

    // Navigation
    document.querySelectorAll('nav a').forEach(link => {
        link.addEventListener('click', (e) => {
            e.preventDefault();
            document.querySelectorAll('nav a').forEach(a => a.classList.remove('active'));
            link.classList.add('active');
            currentView = link.dataset.view;
            loadView(currentView);
        });
    });

    // Export Schema button
    $('#export-schema-btn')?.addEventListener('click', async () => {
        try {
            const res = await fetch(`${basePath}/api/schema/export`);
            if (!res.ok) throw new Error('Export failed');
            const blob = await res.blob();
            const a = document.createElement('a');
            a.href = URL.createObjectURL(blob);
            a.download = 'sqlzibar-schema.yaml';
            a.click();
            URL.revokeObjectURL(a.href);
        } catch (err) {
            alert('Failed to export schema: ' + (err.message || 'Unknown error'));
        }
    });

    // Import Schema button
    $('#import-schema-btn')?.addEventListener('click', () => {
        const input = document.createElement('input');
        input.type = 'file';
        input.accept = '.yaml,.yml';
        input.onchange = async (e) => {
            const file = e.target.files[0];
            if (!file) return;
            try {
                const yaml = await file.text();
                const res = await fetch(`${basePath}/api/schema/import`, {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/x-yaml' },
                    body: yaml
                });
                const result = await res.json();
                if (!res.ok) throw new Error(result.error || 'Import failed');
                alert('Schema imported successfully!');
                loadStats();
                loadView(currentView);
            } catch (err) {
                alert('Failed to import schema: ' + (err.message || 'Unknown error'));
            }
        };
        input.click();
    });

    // Load stats
    async function loadStats() {
        const stats = await api('stats');
        $('#stats').innerHTML = Object.entries(stats).map(([k, v]) =>
            `<div class="stat-card"><div class="label">${k}</div><div class="value">${v}</div></div>`
        ).join('');
    }

    // Load views
    async function loadView(view) {
        subjectDetailContext = null;
        resourceDetailContext = null;
        content.innerHTML = '<div class="loading">Loading...</div>';
        switch(view) {
            case 'resources': return loadResources();
            case 'subjects': return loadSubjects();
            case 'users': return loadUsers();
            case 'agents': return loadAgents();
            case 'service-accounts': return loadServiceAccounts();
            case 'user-groups': return loadUserGroups();
            case 'grants': return loadGrants();
            case 'roles': return loadRoles();
            case 'permissions': return loadPermissions();
            case 'access-tester': return loadAccessTester();
        }
    }

    // --- Shared pagination helpers ---

    function renderPagination(page, totalPages, totalCount) {
        if (totalPages <= 1) return `<div class="pagination-info">${totalCount} item${totalCount !== 1 ? 's' : ''}</div>`;
        let html = '<div class="pagination">';
        html += `<button class="pg-btn" data-page="${page - 1}" ${page <= 1 ? 'disabled' : ''}>Prev</button>`;
        // Show page numbers with ellipsis
        const pages = buildPageNumbers(page, totalPages);
        pages.forEach(p => {
            if (p === '...') {
                html += '<span class="pg-ellipsis">...</span>';
            } else {
                html += `<button class="pg-btn ${p === page ? 'pg-active' : ''}" data-page="${p}">${p}</button>`;
            }
        });
        html += `<button class="pg-btn" data-page="${page + 1}" ${page >= totalPages ? 'disabled' : ''}>Next</button>`;
        html += `<span class="pg-info">${totalCount} item${totalCount !== 1 ? 's' : ''}</span>`;
        html += '</div>';
        return html;
    }

    function buildPageNumbers(current, total) {
        if (total <= 7) return Array.from({length: total}, (_, i) => i + 1);
        const pages = [];
        pages.push(1);
        if (current > 3) pages.push('...');
        for (let i = Math.max(2, current - 1); i <= Math.min(total - 1, current + 1); i++) pages.push(i);
        if (current < total - 2) pages.push('...');
        pages.push(total);
        return pages;
    }

    function bindPagination(containerSel, callback) {
        document.querySelectorAll(`${containerSel} .pg-btn:not([disabled])`).forEach(btn => {
            btn.addEventListener('click', () => callback(parseInt(btn.dataset.page)));
        });
    }

    function renderSearchBox(id, placeholder) {
        return `<div class="search-box"><input type="text" placeholder="${placeholder}" id="${id}"></div>`;
    }

    // --- Resource Tree (lazy-loading) ---

    let treeNodes = new Map(); // id -> node state
    let treeRootIds = [];

    async function loadResources() {
        const result = await api('resources/tree?maxDepth=2');
        if (result.error) {
            content.innerHTML = `<div class="card"><p style="color:red">Error loading resources: ${esc(result.error)}</p></div>`;
            return;
        }
        treeNodes = new Map();
        treeRootIds = result.rootIds || [];

        // Build node state from initial load
        const nodes = result.nodes || [];
        nodes.forEach(n => {
            treeNodes.set(n.id, {
                ...n,
                expanded: false,
                childrenLoaded: false,
                childrenPage: 1,
                hasMoreChildren: false,
                isLoading: false
            });
        });

        // Mark nodes whose children were included in initial load as loaded
        nodes.forEach(n => {
            if (n.parentId && treeNodes.has(n.parentId)) {
                const parent = treeNodes.get(n.parentId);
                if (!parent.childrenLoaded) {
                    parent.childrenLoaded = true;
                    // Check if all children were loaded
                    const loadedChildCount = result.nodes.filter(c => c.parentId === n.parentId).length;
                    parent.hasMoreChildren = loadedChildCount < parent.childCount;
                }
            }
        });

        // Auto-expand roots
        treeRootIds.forEach(id => {
            if (treeNodes.has(id)) treeNodes.get(id).expanded = true;
        });

        renderResourceTree();
    }

    function renderResourceTree() {
        content.innerHTML = `<div class="card"><h3 style="margin-bottom:1rem">Resource Hierarchy</h3><div id="tree"></div></div>`;
        $('#tree').innerHTML = treeRootIds.map(id => renderTreeNode(id)).join('');
        bindTreeEvents();
    }

    function renderTreeNode(nodeId) {
        const n = treeNodes.get(nodeId);
        if (!n) return '';
        const hasChildren = n.childCount > 0;
        const toggleIcon = !hasChildren ? '&nbsp;' : (n.expanded ? '&#9662;' : '&#9656;');
        const childIds = getChildIds(nodeId);

        let html = `<div class="tree-node" data-id="${esc(n.id)}">
            <span class="toggle" data-id="${esc(n.id)}">${toggleIcon}</span>
            <strong class="tree-resource-name" data-id="${esc(n.id)}">${esc(n.name)}</strong>
            <span class="badge badge-blue">${esc(n.resourceType)}</span>`;
        if (n.childCount > 0) html += `<span class="badge badge-gray">${n.childCount} children</span>`;
        if (n.grantsCount > 0) html += `<span class="badge badge-green grants-badge" data-resource-id="${esc(n.id)}" title="Click to view grants">${n.grantsCount} grants</span>`;
        html += `<span class="tree-id">${esc(n.id)}</span>`;

        if (hasChildren && n.expanded) {
            html += '<div class="tree-children">';
            if (n.isLoading && childIds.length === 0) {
                html += '<div class="tree-loading">Loading...</div>';
            } else {
                html += childIds.map(cid => renderTreeNode(cid)).join('');
                if (n.isLoading) html += '<div class="tree-loading">Loading more...</div>';
                if (n.hasMoreChildren && !n.isLoading) {
                    const loaded = childIds.length;
                    const remaining = n.childCount - loaded;
                    html += `<button class="tree-load-more" data-id="${esc(n.id)}">Load more (${remaining} remaining)</button>`;
                }
            }
            html += '</div>';
        }
        html += '</div>';
        return html;
    }

    function getChildIds(parentId) {
        const ids = [];
        treeNodes.forEach((node, id) => {
            if (node.parentId === parentId) ids.push(id);
        });
        // Sort by name
        ids.sort((a, b) => (treeNodes.get(a).name || '').localeCompare(treeNodes.get(b).name || ''));
        return ids;
    }

    function bindTreeEvents() {
        document.querySelectorAll('.toggle[data-id]').forEach(el => {
            el.addEventListener('click', (e) => { e.stopPropagation(); handleToggle(el.dataset.id); });
        });
        document.querySelectorAll('.tree-resource-name[data-id]').forEach(el => {
            el.addEventListener('click', (e) => { e.stopPropagation(); loadResourceDetail(el.dataset.id); });
        });
        document.querySelectorAll('.tree-load-more[data-id]').forEach(el => {
            el.addEventListener('click', () => handleLoadMore(el.dataset.id));
        });
        document.querySelectorAll('.grants-badge[data-resource-id]').forEach(el => {
            el.addEventListener('click', (e) => { e.stopPropagation(); showGrantsPopup(el, el.dataset.resourceId); });
        });
    }

    // --- Grants Popup ---
    let activeGrantsPopup = null;

    function closeGrantsPopup() {
        if (activeGrantsPopup) {
            activeGrantsPopup.remove();
            activeGrantsPopup = null;
        }
    }

    document.addEventListener('click', (e) => {
        if (activeGrantsPopup && !activeGrantsPopup.contains(e.target) && !e.target.classList.contains('grants-badge')) {
            closeGrantsPopup();
        }
    });

    async function showGrantsPopup(badge, resourceId) {
        closeGrantsPopup();

        const popup = document.createElement('div');
        popup.className = 'grants-popup visible';
        popup.innerHTML = `
            <div class="grants-popup-header">
                <h4>Grants on this resource</h4>
                <button class="grants-popup-close" title="Close">&times;</button>
            </div>
            <div class="grants-popup-content">
                <div class="grants-popup-loading">Loading grants...</div>
            </div>
        `;

        badge.style.position = 'relative';
        badge.appendChild(popup);
        activeGrantsPopup = popup;

        popup.querySelector('.grants-popup-close').addEventListener('click', (e) => {
            e.stopPropagation();
            closeGrantsPopup();
        });

        await loadGrantsPopupPage(resourceId, 1);
    }

    async function loadGrantsPopupPage(resourceId, page) {
        if (!activeGrantsPopup) return;

        const contentEl = activeGrantsPopup.querySelector('.grants-popup-content');
        contentEl.innerHTML = '<div class="grants-popup-loading">Loading...</div>';

        try {
            const result = await api(`resources/${encodeURIComponent(resourceId)}/grants?page=${page}&pageSize=10`);

            if (!activeGrantsPopup) return;

            if (result.total === 0) {
                contentEl.innerHTML = '<div class="grants-popup-empty">No direct grants on this resource</div>';
                return;
            }

            let html = `<table class="grants-popup-table">
                <thead><tr><th>Subject</th><th>Type</th><th>Role</th></tr></thead>
                <tbody>`;
            result.data.forEach(g => {
                html += `<tr>
                    <td><span class="grants-popup-subject" title="${esc(g.subjectId)}">${esc(g.subjectName)}</span></td>
                    <td><span class="grants-popup-subject-type">${esc(g.subjectType || '-')}</span></td>
                    <td><span class="grants-popup-role">${esc(g.roleName)}</span></td>
                </tr>`;
            });
            html += '</tbody></table>';
            contentEl.innerHTML = html;

            // Always show pagination controls (at top, in header area)
            let paginationEl = activeGrantsPopup.querySelector('.grants-popup-pagination-bar');
            if (!paginationEl) {
                paginationEl = document.createElement('div');
                paginationEl.className = 'grants-popup-pagination-bar';
                const headerEl = activeGrantsPopup.querySelector('.grants-popup-header');
                headerEl.parentNode.insertBefore(paginationEl, headerEl.nextSibling);
            }
            const startItem = (page - 1) * result.pageSize + 1;
            const endItem = Math.min(page * result.pageSize, result.total);
            paginationEl.innerHTML = `
                <div class="grants-popup-pagination">
                    <button class="grants-popup-prev" ${page <= 1 ? 'disabled' : ''}>&laquo;</button>
                    <span class="grants-popup-page-info">Page ${page} of ${result.totalPages}</span>
                    <button class="grants-popup-next" ${page >= result.totalPages ? 'disabled' : ''}>&raquo;</button>
                </div>
                <span class="grants-popup-info">Showing ${startItem}-${endItem} of ${result.total}</span>
            `;
            paginationEl.querySelector('.grants-popup-prev')?.addEventListener('click', (e) => {
                e.stopPropagation();
                if (page > 1) loadGrantsPopupPage(resourceId, page - 1);
            });
            paginationEl.querySelector('.grants-popup-next')?.addEventListener('click', (e) => {
                e.stopPropagation();
                if (page < result.totalPages) loadGrantsPopupPage(resourceId, page + 1);
            });
        } catch (err) {
            if (activeGrantsPopup) {
                contentEl.innerHTML = `<div class="grants-popup-empty">Error: ${esc(err.message)}</div>`;
            }
        }
    }

    async function loadResourceDetail(resourceId) {
        content.innerHTML = '<div class="loading">Loading...</div>';
        try {
            const [detail, access] = await Promise.all([
                api(`resources/${encodeURIComponent(resourceId)}`),
                api(`resources/${encodeURIComponent(resourceId)}/access`)
            ]);
            renderResourceDetail(detail, access);
        } catch (e) {
            content.innerHTML = `<div class="card"><p class="tester-error">Failed to load resource: ${e.message}</p></div>`;
        }
    }

    function renderResourceDetail(detail, accessList) {
        const r = detail.resource;
        const breadcrumbs = detail.breadcrumbs || [];
        let html = `<div class="detail-header">
            <button class="detail-back" id="back-to-resources">&larr; Back to Resources</button>
            <h2>${esc(r.name)}</h2>
            <span class="badge badge-blue">${esc(r.resourceType)}</span>
        </div>`;

        if (breadcrumbs.length > 0) {
            html += `<div class="breadcrumbs" style="margin-bottom:1rem;font-size:0.9rem;color:#666">
                ${breadcrumbs.map((b, i) => `${i > 0 ? ' &rarr; ' : ''}<span>${esc(b.name)}</span>`).join('')} &rarr; <strong>${esc(r.name)}</strong>
            </div>`;
        }

        html += `<div class="detail-grid">
            <div class="card detail-info">
                <h3>Resource Info</h3>
                <dl class="detail-dl">
                    <dt>ID</dt><dd><code>${esc(r.id)}</code></dd>
                    <dt>Name</dt><dd>${esc(r.name)}</dd>
                    <dt>Type</dt><dd>${esc(r.resourceType)}</dd>
                    <dt>Children</dt><dd>${r.childCount ?? 0}</dd>
                    <dt>Direct Grants</dt><dd>${r.grantsCount ?? 0}</dd>
                </dl>
            </div>
            <div class="card detail-sidebar" style="flex:1">
                <h3 style="margin-bottom:0.5rem">Who Has Access</h3>
                <button class="tester-btn" id="add-grant-resource-btn" style="margin-bottom:1rem" data-resource-id="${esc(r.id)}">Add Grant</button>
                ${accessList.length === 0
                    ? '<p style="color:#888;font-size:0.9rem">No subjects have access to this resource.</p>'
                    : `<table><thead><tr><th>Subject</th><th>Role</th><th>Source</th><th>Inherited</th></tr></thead><tbody>
                        ${accessList.map(a => `<tr>
                            <td>${esc(a.subjectName)}</td>
                            <td><span class="badge badge-blue">${esc(a.roleName)}</span></td>
                            <td>${esc(a.sourceResourceName)}</td>
                            <td>${a.isInherited ? 'Yes' : 'No'}</td>
                        </tr>`).join('')}</tbody></table>`
                }
            </div>
        </div>`;

        content.innerHTML = html;
        resourceDetailContext = r.id;
        $('#back-to-resources').addEventListener('click', () => { resourceDetailContext = null; loadResources(); });
        $('#add-grant-resource-btn').addEventListener('click', () => openGrantModal(null, r.id));
    }

    async function handleToggle(nodeId) {
        const node = treeNodes.get(nodeId);
        if (!node || node.childCount === 0) return;

        node.expanded = !node.expanded;

        // If expanding and children not loaded, fetch them
        if (node.expanded && !node.childrenLoaded) {
            node.isLoading = true;
            renderResourceTree();
            try {
                const result = await api(`resources/${encodeURIComponent(nodeId)}/children?page=1&pageSize=50`);
                result.data.forEach(child => {
                    if (!treeNodes.has(child.id)) {
                        treeNodes.set(child.id, {
                            ...child,
                            expanded: false,
                            childrenLoaded: false,
                            childrenPage: 1,
                            hasMoreChildren: false,
                            isLoading: false
                        });
                    }
                });
                node.childrenLoaded = true;
                node.childrenPage = 1;
                node.hasMoreChildren = result.hasNextPage;
            } catch (e) {
                console.error('Failed to load children:', e);
            }
            node.isLoading = false;
        }

        renderResourceTree();
    }

    async function handleLoadMore(nodeId) {
        const node = treeNodes.get(nodeId);
        if (!node || !node.hasMoreChildren) return;

        node.isLoading = true;
        renderResourceTree();

        try {
            const nextPage = node.childrenPage + 1;
            const result = await api(`resources/${encodeURIComponent(nodeId)}/children?page=${nextPage}&pageSize=50`);
            result.data.forEach(child => {
                if (!treeNodes.has(child.id)) {
                    treeNodes.set(child.id, {
                        ...child,
                        expanded: false,
                        childrenLoaded: false,
                        childrenPage: 1,
                        hasMoreChildren: false,
                        isLoading: false
                    });
                }
            });
            node.childrenPage = nextPage;
            node.hasMoreChildren = result.hasNextPage;
        } catch (e) {
            console.error('Failed to load more children:', e);
        }
        node.isLoading = false;
        renderResourceTree();
    }

    // --- Paginated table views ---

    async function loadSubjects(type, page, search) {
        type = type || 'user';
        page = page || 1;
        search = search || '';
        const params = `type=${type}&page=${page}&pageSize=25${search ? `&search=${encodeURIComponent(search)}` : ''}`;
        const result = await api(`subjects?${params}`);

        content.innerHTML = `<div class="card">
            <div class="tabs">
                <button data-type="user" class="${type==='user'?'active':''}">Users</button>
                <button data-type="group" class="${type==='group'?'active':''}">Groups</button>
                <button data-type="service_account" class="${type==='service_account'?'active':''}">Service Accounts</button>
            </div>
            ${renderSearchBox('subjects-search', 'Search subjects...')}
            <table><thead><tr><th>Display Name</th><th>ID</th><th>Type</th><th>Created</th></tr></thead>
            <tbody>${result.data.map(s => `<tr class="subject-row" data-id="${esc(s.id)}" style="cursor:pointer">
                <td>${esc(s.displayName)}</td><td style="font-size:0.8rem;color:#888">${esc(s.id)}</td>
                <td><span class="badge badge-green">${esc(s.subjectType)}</span></td>
                <td>${new Date(s.createdAt).toLocaleDateString()}</td>
            </tr>`).join('')}</tbody></table>
            <div id="subjects-pagination">${renderPagination(result.page, result.totalPages, result.totalCount)}</div>
        </div>`;

        document.querySelectorAll('.tabs button').forEach(b => {
            b.addEventListener('click', () => loadSubjects(b.dataset.type, 1, ''));
        });
        document.querySelectorAll('.subject-row').forEach(row => {
            row.addEventListener('click', () => loadSubjectDetail(row.dataset.id));
        });
        bindPagination('#subjects-pagination', (p) => loadSubjects(type, p, search));
        const searchInput = $('#subjects-search');
        if (searchInput) {
            searchInput.value = search;
            let debounce;
            searchInput.addEventListener('input', (e) => {
                clearTimeout(debounce);
                debounce = setTimeout(() => loadSubjects(type, 1, e.target.value), 300);
            });
        }
    }

    async function loadUsers(page, search) {
        page = page || 1;
        search = search || '';
        const params = `page=${page}&pageSize=25${search ? `&search=${encodeURIComponent(search)}` : ''}`;
        const result = await api(`users?${params}`);

        content.innerHTML = `<div class="card">
            ${renderSearchBox('users-search', 'Search users...')}
            <table><thead><tr><th>Display Name</th><th>Email</th><th>Active</th><th>Created</th></tr></thead>
            <tbody>${result.data.map(u => `<tr class="subject-row" data-id="${esc(u.subjectId)}" style="cursor:pointer">
                <td>${esc(u.displayName)}</td><td>${esc(u.email || '-')}</td>
                <td>${u.isActive ? 'Yes' : 'No'}</td>
                <td>${new Date(u.createdAt).toLocaleDateString()}</td>
            </tr>`).join('')}</tbody></table>
            <div id="users-pagination">${renderPagination(result.page, result.totalPages, result.totalCount)}</div>
        </div>`;

        document.querySelectorAll('.subject-row').forEach(row => {
            row.addEventListener('click', () => loadSubjectDetail(row.dataset.id, 'users'));
        });
        bindPagination('#users-pagination', (p) => loadUsers(p, search));
        const searchInput = $('#users-search');
        if (searchInput) {
            searchInput.value = search;
            let debounce;
            searchInput.addEventListener('input', (e) => {
                clearTimeout(debounce);
                debounce = setTimeout(() => loadUsers(1, e.target.value), 300);
            });
        }
    }

    async function loadAgents(page, search) {
        page = page || 1;
        search = search || '';
        const params = `page=${page}&pageSize=25${search ? `&search=${encodeURIComponent(search)}` : ''}`;
        const result = await api(`agents?${params}`);

        content.innerHTML = `<div class="card">
            ${renderSearchBox('agents-search', 'Search agents...')}
            <table><thead><tr><th>Display Name</th><th>Type</th><th>Description</th><th>Created</th></tr></thead>
            <tbody>${result.data.map(a => `<tr class="subject-row" data-id="${esc(a.subjectId)}" style="cursor:pointer">
                <td>${esc(a.displayName)}</td><td>${esc(a.agentType || '-')}</td>
                <td>${esc((a.description || '').slice(0, 50))}${(a.description || '').length > 50 ? '...' : ''}</td>
                <td>${new Date(a.createdAt).toLocaleDateString()}</td>
            </tr>`).join('')}</tbody></table>
            <div id="agents-pagination">${renderPagination(result.page, result.totalPages, result.totalCount)}</div>
        </div>`;

        document.querySelectorAll('.subject-row').forEach(row => {
            row.addEventListener('click', () => loadSubjectDetail(row.dataset.id, 'agents'));
        });
        bindPagination('#agents-pagination', (p) => loadAgents(p, search));
        const searchInput = $('#agents-search');
        if (searchInput) {
            searchInput.value = search;
            let debounce;
            searchInput.addEventListener('input', (e) => {
                clearTimeout(debounce);
                debounce = setTimeout(() => loadAgents(1, e.target.value), 300);
            });
        }
    }

    async function loadServiceAccounts(page, search) {
        page = page || 1;
        search = search || '';
        const params = `page=${page}&pageSize=25${search ? `&search=${encodeURIComponent(search)}` : ''}`;
        const result = await api(`service-accounts?${params}`);

        content.innerHTML = `<div class="card">
            ${renderSearchBox('sa-search', 'Search service accounts...')}
            <table><thead><tr><th>Display Name</th><th>Client ID</th><th>Description</th><th>Created</th></tr></thead>
            <tbody>${result.data.map(s => `<tr class="subject-row" data-id="${esc(s.subjectId)}" style="cursor:pointer">
                <td>${esc(s.displayName)}</td><td><code>${esc(s.clientId)}</code></td>
                <td>${esc((s.description || '').slice(0, 40))}${(s.description || '').length > 40 ? '...' : ''}</td>
                <td>${new Date(s.createdAt).toLocaleDateString()}</td>
            </tr>`).join('')}</tbody></table>
            <div id="sa-pagination">${renderPagination(result.page, result.totalPages, result.totalCount)}</div>
        </div>`;

        document.querySelectorAll('.subject-row').forEach(row => {
            row.addEventListener('click', () => loadSubjectDetail(row.dataset.id, 'service-accounts'));
        });
        bindPagination('#sa-pagination', (p) => loadServiceAccounts(p, search));
        const searchInput = $('#sa-search');
        if (searchInput) {
            searchInput.value = search;
            let debounce;
            searchInput.addEventListener('input', (e) => {
                clearTimeout(debounce);
                debounce = setTimeout(() => loadServiceAccounts(1, e.target.value), 300);
            });
        }
    }

    async function loadUserGroups(page, search) {
        page = page || 1;
        search = search || '';
        const params = `page=${page}&pageSize=25${search ? `&search=${encodeURIComponent(search)}` : ''}`;
        const result = await api(`user-groups?${params}`);

        content.innerHTML = `<div class="card">
            ${renderSearchBox('groups-search', 'Search groups...')}
            <table><thead><tr><th>Name</th><th>Type</th><th>Members</th><th>Created</th></tr></thead>
            <tbody>${result.data.map(g => `<tr class="subject-row" data-id="${esc(g.subjectId)}" style="cursor:pointer">
                <td>${esc(g.name)}</td><td>${esc(g.groupType || '-')}</td>
                <td><span class="badge badge-gray">${g.memberCount}</span></td>
                <td>${new Date(g.createdAt).toLocaleDateString()}</td>
            </tr>`).join('')}</tbody></table>
            <div id="groups-pagination">${renderPagination(result.page, result.totalPages, result.totalCount)}</div>
        </div>`;

        document.querySelectorAll('.subject-row').forEach(row => {
            row.addEventListener('click', () => loadSubjectDetail(row.dataset.id, 'user-groups'));
        });
        bindPagination('#groups-pagination', (p) => loadUserGroups(p, search));
        const searchInput = $('#groups-search');
        if (searchInput) {
            searchInput.value = search;
            let debounce;
            searchInput.addEventListener('input', (e) => {
                clearTimeout(debounce);
                debounce = setTimeout(() => loadUserGroups(1, e.target.value), 300);
            });
        }
    }

    // --- Subject Detail ---

    async function loadSubjectDetail(subjectId, backView) {
        backView = backView || 'subjects';
        content.innerHTML = '<div class="loading">Loading...</div>';
        const [detail, grantsResult] = await Promise.all([
            api(`subjects/${encodeURIComponent(subjectId)}`),
            api(`subjects/${encodeURIComponent(subjectId)}/grants?page=1&pageSize=25`)
        ]);
        renderSubjectDetail(detail, grantsResult, backView);
    }

    function renderSubjectDetail(detail, grantsResult, backView) {
        backView = backView || 'subjects';
        const s = detail.subject;
        const backLabels = { 'users': 'Users', 'agents': 'Agents', 'service-accounts': 'Service Accounts', 'user-groups': 'User Groups', 'subjects': 'Subjects' };
        const backLabel = backLabels[backView] || 'Subjects';
        let html = `<div class="detail-header">
            <button class="detail-back" id="back-to-subjects" data-back-view="${esc(backView)}">&larr; Back to ${backLabel}</button>
            <h2>${esc(s.displayName)}</h2>
            <span class="badge badge-green">${esc(s.subjectType)}</span>
        </div>`;

        // Info card
        html += `<div class="detail-grid">
            <div class="card detail-info">
                <h3>Subject Info</h3>
                <dl class="detail-dl">
                    <dt>ID</dt><dd><code>${esc(s.id)}</code></dd>
                    <dt>Display Name</dt><dd>${esc(s.displayName)}</dd>
                    <dt>Type</dt><dd>${esc(s.subjectType)}</dd>
                    ${s.organizationId ? `<dt>Organization</dt><dd>${esc(s.organizationId)}</dd>` : ''}
                    ${s.externalRef ? `<dt>External Ref</dt><dd>${esc(s.externalRef)}</dd>` : ''}
                    <dt>Created</dt><dd>${new Date(s.createdAt).toLocaleString()}</dd>
                    <dt>Updated</dt><dd>${new Date(s.updatedAt).toLocaleString()}</dd>
                </dl>
            </div>`;

        // Groups / Members sidebar
        html += `<div class="card detail-sidebar">`;
        if (detail.groups.length > 0) {
            html += `<h3>Member Of</h3>
                <div class="detail-tags">
                    ${detail.groups.map(g => `<span class="badge badge-blue" style="margin:2px;cursor:pointer" data-group-subject="${esc(g.subjectId)}">${esc(g.name)}${g.groupType ? ` (${esc(g.groupType)})` : ''}</span>`).join('')}
                </div>`;
        }
        if (detail.members.length > 0) {
            html += `<h3 ${detail.groups.length > 0 ? 'style="margin-top:1rem"' : ''}>Group Members</h3>
                <table><thead><tr><th>Name</th><th>Type</th></tr></thead>
                <tbody>${detail.members.map(m => `<tr class="member-row" data-id="${esc(m.id)}" style="cursor:pointer">
                    <td>${esc(m.displayName)}</td>
                    <td><span class="badge badge-green">${esc(m.subjectTypeId)}</span></td>
                </tr>`).join('')}</tbody></table>`;
        }
        if (detail.groups.length === 0 && detail.members.length === 0) {
            html += `<h3>Groups</h3><p style="color:#888;font-size:0.9rem">Not a member of any group.</p>`;
        }
        html += `</div></div>`;

        // Grants table
        html += `<div class="card" style="margin-top:1rem">
            <h3 style="margin-bottom:1rem;display:flex;align-items:center;gap:1rem">
                Role Grants
                <button class="btn-primary btn-sm" id="grant-role-btn">Grant Role</button>
            </h3>
            ${renderSubjectGrantsTable(grantsResult, s.id)}
        </div>`;

        content.innerHTML = html;
        subjectDetailContext = { subjectId: s.id, backView };
        bindSubjectDetailEvents(s.id, backView);
    }

    function renderSubjectGrantsTable(result, subjectId) {
        if (result.data.length === 0) {
            return '<p style="color:#888;font-size:0.9rem">No grants found for this subject.</p>';
        }
        let html = `<table><thead><tr>
            <th>Role</th><th>Resource</th><th>Effective From</th><th>Effective To</th><th>Created</th><th></th>
        </tr></thead><tbody>`;
        result.data.forEach(g => {
            html += `<tr data-grant-id="${esc(g.id)}">
                <td><span class="badge badge-blue">${esc(g.roleName)}</span></td>
                <td>${esc(g.resourceName)}<span class="tree-id">${esc(g.resourceId)}</span></td>
                <td>${g.effectiveFrom ? new Date(g.effectiveFrom).toLocaleDateString() : '-'}</td>
                <td>${g.effectiveTo ? new Date(g.effectiveTo).toLocaleDateString() : '-'}</td>
                <td>${new Date(g.createdAt).toLocaleDateString()}</td>
                <td><button class="btn-danger btn-sm revoke-btn" data-grant-id="${esc(g.id)}">Revoke</button></td>
            </tr>`;
        });
        html += `</tbody></table>`;
        html += `<div id="subject-grants-pagination">${renderPagination(result.page, result.totalPages, result.totalCount)}</div>`;
        return html;
    }

    function bindSubjectDetailEvents(subjectId, backView) {
        backView = backView || 'subjects';
        $('#back-to-subjects').addEventListener('click', () => { subjectDetailContext = null; loadView(backView); });
        // Paginate grants
        $('#grant-role-btn')?.addEventListener('click', () => openGrantModal(subjectId, null));
        document.querySelectorAll('.revoke-btn').forEach(btn => {
            btn.addEventListener('click', async (e) => {
                e.stopPropagation();
                const grantId = btn.dataset.grantId;
                if (!confirm('Revoke this grant?')) return;
                try {
                    const resp = await apiDelete(`grants/${grantId}`);
                    if (!resp.ok) throw new Error('Failed to revoke');
                    loadStats();
                    const r = await api(`subjects/${encodeURIComponent(subjectId)}/grants?page=1&pageSize=25`);
                    const card = document.querySelector('#subject-grants-pagination')?.closest('.card');
                    if (card) card.innerHTML = '<h3 style="margin-bottom:1rem;display:flex;align-items:center;gap:1rem">Role Grants<button class="btn-primary btn-sm" id="grant-role-btn">Grant Role</button></h3>' + renderSubjectGrantsTable(r, subjectId);
                    bindSubjectDetailEvents(subjectId, backView);
                } catch (err) {
                    alert('Error: ' + (err.message || 'Unknown error'));
                }
            });
        });
        bindPagination('#subject-grants-pagination', async (page) => {
            const grantsResult = await api(`subjects/${encodeURIComponent(subjectId)}/grants?page=${page}&pageSize=25`);
            const grantsCard = document.querySelector('#subject-grants-pagination').closest('.card');
            grantsCard.innerHTML = '<h3 style="margin-bottom:1rem;display:flex;align-items:center;gap:1rem">Role Grants<button class="btn-primary btn-sm" id="grant-role-btn">Grant Role</button></h3>' + renderSubjectGrantsTable(grantsResult, subjectId);
            bindSubjectDetailEvents(subjectId, backView);
            bindPagination('#subject-grants-pagination', async (p) => {
                const r = await api(`subjects/${encodeURIComponent(subjectId)}/grants?page=${p}&pageSize=25`);
                const card = document.querySelector('#subject-grants-pagination').closest('.card');
                card.innerHTML = '<h3 style="margin-bottom:1rem;display:flex;align-items:center;gap:1rem">Role Grants<button class="btn-primary btn-sm" id="grant-role-btn">Grant Role</button></h3>' + renderSubjectGrantsTable(r, subjectId);
                bindSubjectDetailEvents(subjectId, backView);
            });
        });
        // Click group badges to navigate to that group's detail
        document.querySelectorAll('[data-group-subject]').forEach(el => {
            el.addEventListener('click', () => loadSubjectDetail(el.dataset.groupSubject, backView));
        });
        // Click member rows to navigate to that member's detail
        document.querySelectorAll('.member-row').forEach(row => {
            row.addEventListener('click', () => loadSubjectDetail(row.dataset.id, backView));
        });
    }

    async function loadGrants(page, search) {
        page = page || 1;
        search = search || '';
        const params = `page=${page}&pageSize=25${search ? `&search=${encodeURIComponent(search)}` : ''}`;
        const result = await api(`grants?${params}`);

        content.innerHTML = `<div class="card">
            <div style="display:flex;align-items:center;gap:1rem;margin-bottom:1rem;flex-wrap:wrap">
                <div style="flex:1;min-width:200px">${renderSearchBox('grants-search', 'Search grants...')}</div>
                <button class="btn-primary btn-sm" id="add-grant-btn">Add Grant</button>
            </div>
            <table><thead><tr>
                <th>Subject</th><th>Role</th><th>Resource</th><th>Effective From</th><th>Effective To</th><th>Created</th><th></th>
            </tr></thead><tbody>${result.data.map(g => `<tr>
                <td>${esc(g.subjectName)}</td><td><span class="badge badge-blue">${esc(g.roleName)}</span></td>
                <td>${esc(g.resourceName)}</td>
                <td>${g.effectiveFrom ? new Date(g.effectiveFrom).toLocaleDateString() : '-'}</td>
                <td>${g.effectiveTo ? new Date(g.effectiveTo).toLocaleDateString() : '-'}</td>
                <td>${new Date(g.createdAt).toLocaleDateString()}</td>
                <td><button class="btn-danger btn-sm revoke-btn grant-revoke-btn" data-grant-id="${esc(g.id)}">Revoke</button></td>
            </tr>`).join('')}</tbody></table>
            <div id="grants-pagination">${renderPagination(result.page, result.totalPages, result.totalCount)}</div>
        </div>`;

        $('#add-grant-btn')?.addEventListener('click', () => openGrantModal(null, null));
        document.querySelectorAll('.grant-revoke-btn').forEach(btn => {
            btn.addEventListener('click', async (e) => {
                e.stopPropagation();
                const grantId = btn.dataset.grantId;
                if (!confirm('Revoke this grant?')) return;
                try {
                    const resp = await apiDelete(`grants/${grantId}`);
                    if (!resp.ok) throw new Error('Failed to revoke');
                    loadStats();
                    loadGrants(result.page, $('#grants-search')?.value ?? search);
                } catch (err) {
                    alert('Error: ' + (err.message || 'Unknown error'));
                }
            });
        });
        bindPagination('#grants-pagination', (p) => loadGrants(p, search));
        const searchInput = $('#grants-search');
        if (searchInput) {
            searchInput.value = search;
            let debounce;
            searchInput.addEventListener('input', (e) => {
                clearTimeout(debounce);
                debounce = setTimeout(() => loadGrants(1, e.target.value), 300);
            });
        }
    }

    async function loadRoles(page, search) {
        page = page || 1;
        search = search || '';
        const params = `page=${page}&pageSize=25${search ? `&search=${encodeURIComponent(search)}` : ''}`;
        const result = await api(`roles?${params}`);

        content.innerHTML = `<div class="card">
            <div style="display:flex;align-items:center;gap:1rem;margin-bottom:1rem;flex-wrap:wrap">
                <div style="flex:1;min-width:200px">${renderSearchBox('roles-search', 'Search roles...')}</div>
                <button class="btn-primary btn-sm" id="create-role-btn">Create Role</button>
            </div>
            <table><thead><tr>
                <th>Name</th><th>Key</th><th>Permissions</th><th>Virtual</th>
            </tr></thead><tbody>${result.data.map(r => `<tr class="role-row" data-id="${esc(r.id)}" style="cursor:pointer">
                <td>${esc(r.name)}</td><td><code>${esc(r.key)}</code></td>
                <td><span class="badge badge-gray">${r.permissionCount}</span></td>
                <td>${r.isVirtual ? 'Yes' : 'No'}</td>
            </tr>`).join('')}</tbody></table>
            <div id="roles-pagination">${renderPagination(result.page, result.totalPages, result.totalCount)}</div>
        </div>`;

        $('#create-role-btn')?.addEventListener('click', openCreateRoleModal);
        document.querySelectorAll('.role-row').forEach(row => {
            row.addEventListener('click', () => loadRoleDetail(row.dataset.id));
        });
        bindPagination('#roles-pagination', (p) => loadRoles(p, search));
        const searchInput = $('#roles-search');
        if (searchInput) {
            searchInput.value = search;
            let debounce;
            searchInput.addEventListener('input', (e) => {
                clearTimeout(debounce);
                debounce = setTimeout(() => loadRoles(1, e.target.value), 300);
            });
        }
    }

    async function loadRoleDetail(roleId) {
        content.innerHTML = '<div class="loading">Loading...</div>';
        const [role, perms, allPerms] = await Promise.all([
            api(`roles/${encodeURIComponent(roleId)}`),
            api(`roles/${encodeURIComponent(roleId)}/permissions`),
            api('permissions?pageSize=500')
        ]);

        let html = `<div class="detail-header">
            <button class="detail-back" id="back-to-roles">&larr; Back to Roles</button>
            <h2>${esc(role.name)}</h2>
            ${role.isVirtual ? '<span class="badge badge-gray">Virtual</span>' : ''}
        </div>`;

        html += `<div class="detail-grid">
            <div class="card detail-info">
                <h3>Role Info</h3>
                <dl class="detail-dl">
                    <dt>ID</dt><dd><code>${esc(role.id)}</code></dd>
                    <dt>Key</dt><dd><code>${esc(role.key)}</code></dd>
                    <dt>Name</dt><dd>${esc(role.name)}</dd>
                    <dt>Description</dt><dd>${esc(role.description) || '-'}</dd>
                    <dt>Virtual</dt><dd>${role.isVirtual ? 'Yes' : 'No'}</dd>
                </dl>
                <div style="margin-top:1rem">
                    <button class="btn-danger btn-sm" id="delete-role-btn">Delete Role</button>
                </div>
            </div>
            <div class="card detail-sidebar">
                <h3 style="display:flex;align-items:center;gap:1rem">
                    Permissions
                    <button class="btn-primary btn-sm" id="add-perm-btn">Add Permission</button>
                </h3>
                <div id="role-perms-list" style="margin-top:0.75rem">
                    ${perms.length === 0
                        ? '<p style="color:#888;font-size:0.9rem">No permissions assigned.</p>'
                        : perms.map(p => `<div class="role-perm-item" style="display:flex;align-items:center;gap:0.5rem;margin-bottom:0.4rem">
                            <span class="badge badge-blue">${esc(p.key)}</span>
                            <span style="flex:1;font-size:0.85rem;color:#666">${esc(p.name)}</span>
                            <button class="btn-danger btn-sm remove-perm-btn" data-perm-id="${esc(p.id)}">Remove</button>
                        </div>`).join('')
                    }
                </div>
            </div>
        </div>`;

        content.innerHTML = html;

        $('#back-to-roles').addEventListener('click', () => loadRoles());
        $('#delete-role-btn').addEventListener('click', async () => {
            if (!confirm('Delete this role? This cannot be undone.')) return;
            try {
                const resp = await apiDelete(`roles/${encodeURIComponent(roleId)}`);
                if (!resp.ok) {
                    const err = await resp.json().catch(() => ({}));
                    throw new Error(err.error || 'Failed to delete role');
                }
                loadStats();
                loadRoles();
            } catch (e) {
                alert('Error: ' + (e.message || 'Unknown error'));
            }
        });
        $('#add-perm-btn').addEventListener('click', () => openAddPermissionToRoleModal(roleId, perms, allPerms.data));
        document.querySelectorAll('.remove-perm-btn').forEach(btn => {
            btn.addEventListener('click', async () => {
                const permId = btn.dataset.permId;
                if (!confirm('Remove this permission from the role?')) return;
                try {
                    const resp = await apiDelete(`roles/${encodeURIComponent(roleId)}/permissions/${encodeURIComponent(permId)}`);
                    if (!resp.ok) throw new Error('Failed to remove permission');
                    loadRoleDetail(roleId);
                } catch (e) {
                    alert('Error: ' + (e.message || 'Unknown error'));
                }
            });
        });
    }

    function openCreateRoleModal() {
        let html = `<h3>Create Role</h3>
            <div class="modal-field">
                <label>Key</label>
                <input type="text" id="role-key" placeholder="e.g. admin, viewer">
            </div>
            <div class="modal-field">
                <label>Name</label>
                <input type="text" id="role-name" placeholder="Display name">
            </div>
            <div class="modal-field">
                <label>Description</label>
                <input type="text" id="role-desc" placeholder="Optional description">
            </div>
            <div class="modal-actions">
                <button class="btn-secondary" id="role-cancel">Cancel</button>
                <button class="btn-primary" id="role-submit">Create</button>
            </div>`;

        $('#modal').innerHTML = html;
        $('#modal-overlay').style.display = 'flex';

        $('#role-cancel').addEventListener('click', closeModal);
        $('#modal-overlay').onclick = (e) => { if (e.target === $('#modal-overlay')) closeModal(); };

        $('#role-submit').addEventListener('click', async () => {
            const key = $('#role-key').value.trim();
            const name = $('#role-name').value.trim();
            const desc = $('#role-desc').value.trim();
            if (!key || !name) {
                alert('Key and Name are required.');
                return;
            }
            try {
                const resp = await apiPost('roles', { key, name, description: desc || null });
                if (!resp.ok) {
                    const err = await resp.json().catch(() => ({}));
                    throw new Error(err.error || 'Failed to create role');
                }
                closeModal();
                loadStats();
                loadRoles();
            } catch (e) {
                alert('Error: ' + (e.message || 'Unknown error'));
            }
        });
    }

    function openAddPermissionToRoleModal(roleId, existingPerms, allPerms) {
        const existingIds = new Set(existingPerms.map(p => p.id));
        const available = allPerms.filter(p => !existingIds.has(p.id));

        if (available.length === 0) {
            alert('All permissions are already assigned to this role.');
            return;
        }

        let html = `<h3>Add Permission to Role</h3>
            <div class="modal-field">
                <label>Permission</label>
                <select id="add-perm-select">
                    <option value="">Select permission...</option>
                    ${available.map(p => `<option value="${esc(p.id)}">${esc(p.key)} - ${esc(p.name)}</option>`).join('')}
                </select>
            </div>
            <div class="modal-actions">
                <button class="btn-secondary" id="add-perm-cancel">Cancel</button>
                <button class="btn-primary" id="add-perm-submit">Add</button>
            </div>`;

        $('#modal').innerHTML = html;
        $('#modal-overlay').style.display = 'flex';

        $('#add-perm-cancel').addEventListener('click', closeModal);
        $('#modal-overlay').onclick = (e) => { if (e.target === $('#modal-overlay')) closeModal(); };

        $('#add-perm-submit').addEventListener('click', async () => {
            const permId = $('#add-perm-select').value;
            if (!permId) {
                alert('Please select a permission.');
                return;
            }
            try {
                const resp = await apiPost(`roles/${encodeURIComponent(roleId)}/permissions`, { permissionId: permId });
                if (!resp.ok) {
                    const err = await resp.json().catch(() => ({}));
                    throw new Error(err.error || 'Failed to add permission');
                }
                closeModal();
                loadRoleDetail(roleId);
            } catch (e) {
                alert('Error: ' + (e.message || 'Unknown error'));
            }
        });
    }

    async function loadPermissions(page, search) {
        page = page || 1;
        search = search || '';
        const params = `page=${page}&pageSize=25${search ? `&search=${encodeURIComponent(search)}` : ''}`;
        const [result, resourceTypes] = await Promise.all([
            api(`permissions?${params}`),
            api('resource-types?pageSize=100')
        ]);

        content.innerHTML = `<div class="card">
            <div style="display:flex;align-items:center;gap:1rem;margin-bottom:1rem;flex-wrap:wrap">
                <div style="flex:1;min-width:200px">${renderSearchBox('perm-search', 'Search permissions...')}</div>
                <button class="btn-primary btn-sm" id="create-perm-btn">Create Permission</button>
            </div>
            <table><thead><tr><th>Key</th><th>Name</th><th>Resource Type</th></tr></thead>
            <tbody>${result.data.map(p => `<tr>
                <td><code>${esc(p.key)}</code></td><td>${esc(p.name)}</td>
                <td>${p.resourceType ? `<span class="badge badge-green">${esc(p.resourceType)}</span>` : '-'}</td>
            </tr>`).join('')}</tbody></table>
            <div id="perms-pagination">${renderPagination(result.page, result.totalPages, result.totalCount)}</div>
        </div>`;

        $('#create-perm-btn')?.addEventListener('click', () => openCreatePermissionModal(resourceTypes.data || []));
        bindPagination('#perms-pagination', (p) => loadPermissions(p, search));
        const searchInput = $('#perm-search');
        if (searchInput) {
            searchInput.value = search;
            let debounce;
            searchInput.addEventListener('input', (e) => {
                clearTimeout(debounce);
                debounce = setTimeout(() => loadPermissions(1, e.target.value), 300);
            });
        }
    }

    function openCreatePermissionModal(resourceTypes) {
        let html = `<h3>Create Permission</h3>
            <div class="modal-field">
                <label>Key</label>
                <input type="text" id="perm-key" placeholder="e.g. read, write, delete">
            </div>
            <div class="modal-field">
                <label>Name</label>
                <input type="text" id="perm-name" placeholder="Display name">
            </div>
            <div class="modal-field">
                <label>Description</label>
                <input type="text" id="perm-desc" placeholder="Optional description">
            </div>
            <div class="modal-field">
                <label>Resource Type (optional)</label>
                <select id="perm-resource-type">
                    <option value="">Any resource type</option>
                    ${resourceTypes.map(rt => `<option value="${esc(rt.id)}">${esc(rt.name)} (${esc(rt.key)})</option>`).join('')}
                </select>
            </div>
            <div class="modal-actions">
                <button class="btn-secondary" id="perm-cancel">Cancel</button>
                <button class="btn-primary" id="perm-submit">Create</button>
            </div>`;

        $('#modal').innerHTML = html;
        $('#modal-overlay').style.display = 'flex';

        $('#perm-cancel').addEventListener('click', closeModal);
        $('#modal-overlay').onclick = (e) => { if (e.target === $('#modal-overlay')) closeModal(); };

        $('#perm-submit').addEventListener('click', async () => {
            const key = $('#perm-key').value.trim();
            const name = $('#perm-name').value.trim();
            const desc = $('#perm-desc').value.trim();
            const resourceTypeId = $('#perm-resource-type').value || null;
            if (!key || !name) {
                alert('Key and Name are required.');
                return;
            }
            try {
                const resp = await apiPost('permissions', { key, name, description: desc || null, resourceTypeId });
                if (!resp.ok) {
                    const err = await resp.json().catch(() => ({}));
                    throw new Error(err.error || 'Failed to create permission');
                }
                closeModal();
                loadStats();
                loadPermissions();
            } catch (e) {
                alert('Error: ' + (e.message || 'Unknown error'));
            }
        });
    }

    // --- Access Tester ---

    let accessTesterMode = 'check'; // 'check' or 'list'

    async function loadAccessTester() {
        const [subjectsResult, permissionsResult, treeResult] = await Promise.all([
            api('subjects?pageSize=100'), api('permissions?pageSize=100'), api('resources/tree?maxDepth=5')
        ]);
        const subjects = subjectsResult.data;
        const permissions = permissionsResult.data;
        const resources = treeResult.nodes;

        content.innerHTML = `<div class="card">
            <h3 style="margin-bottom:1rem">Access Tester</h3>
            <div class="tabs" style="margin-bottom:1rem">
                <button class="tester-mode-btn ${accessTesterMode === 'check' ? 'active' : ''}" data-mode="check">Check Access</button>
                <button class="tester-mode-btn ${accessTesterMode === 'list' ? 'active' : ''}" data-mode="list">List Access</button>
            </div>
            <div id="tester-content"></div>
        </div>`;

        document.querySelectorAll('.tester-mode-btn').forEach(btn => {
            btn.addEventListener('click', () => {
                accessTesterMode = btn.dataset.mode;
                loadAccessTester();
            });
        });

        if (accessTesterMode === 'check') {
            renderCheckAccessForm(subjects, permissions, resources);
        } else {
            renderListAccessForm(resources);
        }
    }

    function renderCheckAccessForm(subjects, permissions, resources) {
        $('#tester-content').innerHTML = `
            <p style="color:#666;margin-bottom:1.5rem;font-size:0.9rem">
                Test whether a subject has a specific permission on a resource. Returns a detailed trace of how the decision was made.
            </p>
            <div class="tester-form">
                <div class="tester-field">
                    <label>Subject</label>
                    <select id="tester-subject">
                        <option value="">Select a subject...</option>
                        ${subjects.map(s => `<option value="${esc(s.id)}">${esc(s.displayName)} (${esc(s.subjectTypeId)})</option>`).join('')}
                    </select>
                </div>
                <div class="tester-field">
                    <label>Permission</label>
                    <select id="tester-permission">
                        <option value="">Select a permission...</option>
                        ${permissions.map(p => `<option value="${esc(p.key)}">${esc(p.key)} — ${esc(p.name)}</option>`).join('')}
                    </select>
                </div>
                <div class="tester-field">
                    <label>Resource</label>
                    <select id="tester-resource">
                        <option value="">Select a resource...</option>
                        ${resources.map(r => `<option value="${esc(r.id)}">${esc(r.name)} (${esc(r.resourceType)})</option>`).join('')}
                    </select>
                </div>
                <button id="tester-run" class="tester-btn">Test Access</button>
            </div>
            <div id="tester-result"></div>`;

        $('#tester-run').addEventListener('click', runAccessTest);
    }

    function renderListAccessForm(resources) {
        $('#tester-content').innerHTML = `
            <p style="color:#666;margin-bottom:1.5rem;font-size:0.9rem">
                List all subjects who have access to a specific resource (including inherited access).
            </p>
            <div class="tester-form" style="grid-template-columns:1fr auto">
                <div class="tester-field">
                    <label>Resource</label>
                    <select id="list-resource">
                        <option value="">Select a resource...</option>
                        ${resources.map(r => `<option value="${esc(r.id)}">${esc(r.name)} (${esc(r.resourceType)})</option>`).join('')}
                    </select>
                </div>
                <button id="list-run" class="tester-btn">List Access</button>
            </div>
            <div id="list-result"></div>`;

        $('#list-run').addEventListener('click', runListAccess);
    }

    async function runListAccess() {
        const resourceId = $('#list-resource').value;
        if (!resourceId) {
            $('#list-result').innerHTML = '<div class="tester-error">Please select a resource.</div>';
            return;
        }

        $('#list-result').innerHTML = '<div class="loading">Loading access list...</div>';
        $('#list-run').disabled = true;

        try {
            const accessList = await api(`resources/${encodeURIComponent(resourceId)}/access`);
            if (accessList.length === 0) {
                $('#list-result').innerHTML = '<p style="color:#888;margin-top:1rem">No subjects have access to this resource.</p>';
            } else {
                $('#list-result').innerHTML = `<table style="margin-top:1rem">
                    <thead><tr><th>Subject</th><th>Role</th><th>Source Resource</th><th>Inherited</th></tr></thead>
                    <tbody>${accessList.map(a => `<tr>
                        <td>${esc(a.subjectName)}<span class="tree-id">${esc(a.subjectId)}</span></td>
                        <td><span class="badge badge-blue">${esc(a.roleName)}</span></td>
                        <td>${esc(a.sourceResourceName)}<span class="tree-id">${esc(a.sourceResourceId)}</span></td>
                        <td>${a.isInherited ? 'Yes' : 'No'}</td>
                    </tr>`).join('')}</tbody>
                </table>`;
            }
        } catch (e) {
            $('#list-result').innerHTML = `<div class="tester-error">Error: ${e.message}</div>`;
        } finally {
            $('#list-run').disabled = false;
        }
    }

    async function runAccessTest() {
        const subjectId = $('#tester-subject').value;
        const permissionKey = $('#tester-permission').value;
        const resourceId = $('#tester-resource').value;

        if (!subjectId || !permissionKey || !resourceId) {
            $('#tester-result').innerHTML = '<div class="tester-error">Please select a subject, permission, and resource.</div>';
            return;
        }

        $('#tester-result').innerHTML = '<div class="loading">Running trace...</div>';
        $('#tester-run').disabled = true;

        try {
            const trace = await fetch(`${basePath}/api/trace`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ subjectId, permissionKey, resourceId })
            }).then(r => r.json());

            $('#tester-result').innerHTML = renderTraceResult(trace);
        } catch (e) {
            $('#tester-result').innerHTML = `<div class="tester-error">Error: ${e.message}</div>`;
        } finally {
            $('#tester-run').disabled = false;
        }
    }

    function renderTraceResult(t) {
        const granted = t.accessGranted;
        const bannerClass = granted ? 'trace-banner-granted' : 'trace-banner-denied';
        const bannerIcon = granted ? '\u2713' : '\u2717';
        const bannerText = granted ? 'ACCESS GRANTED' : 'ACCESS DENIED';

        let html = `<div class="trace-result">`;
        html += `<div class="trace-banner ${bannerClass}">
            <span class="trace-banner-icon">${bannerIcon}</span> ${bannerText}
        </div>`;

        html += `<div class="trace-section">
            <div class="trace-section-title">Decision Summary</div>
            <div class="trace-summary-text">${esc(t.decisionSummary)}</div>`;
        if (t.denialReason) html += `<div class="trace-denial">${esc(t.denialReason)}</div>`;
        if (t.suggestion) html += `<div class="trace-suggestion">${esc(t.suggestion)}</div>`;
        html += `</div>`;

        if (t.subjectsChecked && t.subjectsChecked.length > 0) {
            html += `<div class="trace-section">
                <div class="trace-section-title">Subjects Checked</div>
                <div class="trace-subjects">
                    ${t.subjectsChecked.map(s => `<span class="badge ${s.isDirect ? 'badge-blue' : 'badge-green'}">${esc(s.displayName)} (${esc(s.type)})${s.isDirect ? '' : ' — via group'}</span>`).join(' ')}
                </div>
            </div>`;
        }

        if (t.pathNodes && t.pathNodes.length > 0) {
            html += `<div class="trace-section">
                <div class="trace-section-title">Resource Path &amp; Grants</div>
                <div class="trace-path">`;
            t.pathNodes.forEach((node, i) => {
                html += `<div class="trace-path-node">
                    <div class="trace-path-connector">${i > 0 ? '<div class="trace-path-line"></div>' : ''}</div>
                    <div class="trace-path-content">
                        <div class="trace-path-header">
                            <strong>${esc(node.name)}</strong>
                            <span class="badge badge-gray">${esc(node.resourceType)}</span>
                            ${node.isTarget ? '<span class="badge badge-blue">Target</span>' : ''}
                            ${node.permissionFoundHere ? '<span class="badge badge-green">\u2713 Permission found here</span>' : ''}
                        </div>
                        <div class="trace-path-id">${esc(node.resourceId)}</div>`;
                if (node.grantsOnThisNode && node.grantsOnThisNode.length > 0) {
                    html += `<div class="trace-path-grants">`;
                    node.grantsOnThisNode.forEach(g => {
                        const grantClass = g.contributedToDecision ? 'trace-grant-contributed' : '';
                        html += `<div class="trace-grant ${grantClass}">
                            <span class="badge badge-blue">${esc(g.roleName)}</span>
                            <span style="margin:0 0.3rem">\u2192</span>
                            <span>${esc(g.subjectDisplayName)}</span>
                            ${g.viaGroupName ? `<span class="trace-via-group">via ${esc(g.viaGroupName)}</span>` : ''}
                            ${g.contributedToDecision ? '<span class="badge badge-green" style="margin-left:0.5rem">\u2713</span>' : ''}
                        </div>`;
                    });
                    html += `</div>`;
                }
                if (node.effectivePermissions && node.effectivePermissions.length > 0) {
                    html += `<div class="trace-path-perms">
                        ${node.effectivePermissions.map(p => `<span class="badge ${p === t.permissionKey ? 'badge-green' : 'badge-gray'}" style="margin:2px">${esc(p)}</span>`).join('')}
                    </div>`;
                }
                html += `</div></div>`;
            });
            html += `</div></div>`;
        }

        if (t.allRolesUsed && t.allRolesUsed.length > 0) {
            html += `<div class="trace-section">
                <div class="trace-section-title">Roles &amp; Permissions Used</div>
                <table class="trace-roles-table"><thead><tr>
                    <th>Role</th><th>Source</th><th>Permissions</th><th>Match?</th>
                </tr></thead><tbody>`;
            t.allRolesUsed.forEach(r => {
                const rowClass = r.contributedToDecision ? 'trace-role-contributed' : '';
                const permBadges = r.permissions.slice(0, 8).map(p =>
                    `<span class="badge ${p.usedForDecision ? 'badge-green' : 'badge-gray'}" style="margin:2px">${esc(p.permissionKey)}</span>`
                ).join('');
                const moreCount = r.permissions.length > 8 ? `<span class="badge badge-gray" style="margin:2px">+${r.permissions.length - 8} more</span>` : '';
                html += `<tr class="${rowClass}">
                    <td><strong>${esc(r.roleName)}</strong> <code>${esc(r.roleKey)}</code>${r.isVirtualRole ? ' <em>(virtual)</em>' : ''}</td>
                    <td>${r.sourceResourceName ? `${esc(r.sourceResourceName)} <span class="badge badge-gray">${esc(r.sourceResourceType || '')}</span>` : '-'}</td>
                    <td>${permBadges}${moreCount}</td>
                    <td>${r.contributedToDecision ? '<span class="trace-match">\u2713</span>' : ''}</td>
                </tr>`;
            });
            html += `</tbody></table></div>`;
        }

        html += `</div>`;
        return html;
    }

    function esc(s) {
        if (!s) return '';
        const d = document.createElement('div');
        d.textContent = s;
        return d.innerHTML;
    }

    function closeModal() {
        $('#modal-overlay').style.display = 'none';
    }

    async function openGrantModal(subjectId, resourceId) {
        const [subjectsRes, rolesRes, treeRes] = await Promise.all([
            api('subjects?pageSize=500'),
            api('roles?pageSize=500'),
            api('resources/tree?maxDepth=5')
        ]);
        const subjects = subjectsRes.data;
        const roles = rolesRes.data;
        const resources = treeRes.nodes || [];

        let html = `<h3>${subjectId ? 'Grant Role to Subject' : resourceId ? 'Add Grant to Resource' : 'Create Grant'}</h3>`;
        if (!subjectId) {
            html += `<div class="modal-field">
                <label>Subject</label>
                <select id="grant-subject">
                    <option value="">Select subject...</option>
                    ${subjects.map(s => `<option value="${esc(s.id)}">${esc(s.displayName)} (${esc(s.subjectTypeId)})</option>`).join('')}
                </select>
            </div>`;
        }
        html += `<div class="modal-field">
            <label>Role</label>
            <select id="grant-role">
                <option value="">Select role...</option>
                ${roles.map(r => `<option value="${esc(r.id)}">${esc(r.name)} (${esc(r.key)})</option>`).join('')}
            </select>
        </div>`;
        if (!resourceId) {
            html += `<div class="modal-field">
                <label>Resource</label>
                <select id="grant-resource">
                    <option value="">Select resource...</option>
                    ${resources.map(r => `<option value="${esc(r.id)}">${esc(r.name)} (${esc(r.resourceType)})</option>`).join('')}
                </select>
            </div>`;
        }
        html += `<div class="modal-actions">
            <button class="btn-secondary" id="grant-cancel">Cancel</button>
            <button class="btn-primary" id="grant-submit">Grant</button>
        </div>`;

        $('#modal').innerHTML = html;
        $('#modal-overlay').style.display = 'flex';

        $('#grant-cancel').addEventListener('click', closeModal);
        $('#modal-overlay').onclick = (e) => { if (e.target === $('#modal-overlay')) closeModal(); };

        $('#grant-submit').addEventListener('click', async () => {
            const subj = subjectId || $('#grant-subject')?.value;
            const role = $('#grant-role').value;
            const res = resourceId || $('#grant-resource')?.value;
            if (!subj || !role || !res) {
                alert('Please select subject, role, and resource.');
                return;
            }
            try {
                const resp = await apiPost('grants', { subjectId: subj, roleId: role, resourceId: res });
                if (!resp.ok) {
                    const err = await resp.json().catch(() => ({}));
                    throw new Error(err.error || 'Failed to create grant');
                }
                closeModal();
                loadStats();
                if (subjectDetailContext) loadSubjectDetail(subjectDetailContext.subjectId, subjectDetailContext.backView);
                else if (resourceDetailContext) loadResourceDetail(resourceDetailContext);
                else loadGrants();
            } catch (e) {
                alert('Error: ' + (e.message || 'Unknown error'));
            }
        });
    }

    // Init
    loadStats();
    loadView('resources');
})();
