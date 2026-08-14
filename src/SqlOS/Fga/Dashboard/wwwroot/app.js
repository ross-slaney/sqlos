(function() {
    const componentMarkup = `
        <div class="fga-dashboard">
            <div id="stats" class="stats-bar"></div>
            <div id="content"></div>
            <div id="modal-overlay" class="modal-overlay" hidden>
                <div id="modal" class="modal" role="dialog" aria-modal="true"></div>
            </div>
        </div>`;

    function normalizeRoute(value) {
        const withoutHash = String(value || '/resources').replace(/^#/, '');
        const withSlash = withoutHash.startsWith('/') ? withoutHash : `/${withoutHash}`;
        return withSlash.replace(/\/{2,}/g, '/');
    }

    function mount(options) {
        if (!options?.host) {
            throw new Error('SqlOS FGA dashboard requires a host element.');
        }

        const host = options.host;
        const basePath = String(options.basePath || '/sqlos/admin/fga').replace(/\/$/, '');
        const dashboardBasePath = String(options.dashboardBasePath || basePath.replace(/\/admin\/fga$/i, '') || '/sqlos').replace(/\/$/, '');
        const root = host.shadowRoot || host.attachShadow({ mode: 'open' });
        root.replaceChildren();

        const stylesheet = document.createElement('link');
        stylesheet.rel = 'stylesheet';
        stylesheet.href = `${basePath}/style.css`;
        root.append(stylesheet);

        const extraStyles = document.createElement('style');
        extraStyles.textContent = `
            .stats-cached { align-self: center; font-size: 0.7rem; }
            .remote-picker { position: relative; }
            .remote-picker-search { width: 100%; padding: 0.45rem 0.6rem; border: 1px solid var(--zinc-200); border-radius: var(--radius); font-size: 0.8125rem; background: #fff; color: var(--zinc-800); }
            .remote-picker-search:focus { outline: none; border-color: var(--amber-400); box-shadow: 0 0 0 2px var(--amber-100); }
            .remote-picker-results { position: absolute; left: 0; right: 0; z-index: 30; max-height: 220px; overflow-y: auto; background: #fff; border: 1px solid var(--zinc-200); border-radius: var(--radius); margin-top: 4px; box-shadow: 0 4px 16px rgba(0,0,0,0.08); }
            .remote-picker-item { display: block; width: 100%; text-align: left; padding: 0.4rem 0.6rem; border: none; background: none; cursor: pointer; font-size: 0.8125rem; color: var(--zinc-800); }
            .remote-picker-item:hover, .remote-picker-item.selected { background: var(--zinc-50); }
            .remote-picker-more { display: block; width: 100%; padding: 0.4rem; border: none; border-top: 1px dashed var(--zinc-200); background: none; color: var(--amber-600); cursor: pointer; font-size: 0.75rem; }
            .remote-picker-more:hover { background: var(--amber-50); }
            .remote-picker-empty { padding: 0.6rem; color: var(--zinc-500); font-size: 0.8rem; }
            .load-more-wrap { margin-top: 0.75rem; }
        `;
        root.append(extraStyles);

        const component = document.createElement('div');
        component.innerHTML = componentMarkup;
        root.append(component.firstElementChild);

        let currentRoute = normalizeRoute(options.initialRoute);
        let destroyed = false;

    function redirectToLogin() {
        const next = encodeURIComponent(`${window.location.pathname}${window.location.search}`);
        window.location.href = `${dashboardBasePath}/login?next=${next}`;
    }
    const api = async (endpoint) => {
        const response = await fetch(`${basePath}/api/${endpoint}`, { credentials: 'same-origin' });
        if (response.status === 401) {
            redirectToLogin();
            throw new Error('Unauthorized');
        }
        if (!response.ok) {
            const text = await response.text();
            throw new Error(text || `${response.status}`);
        }
        return response.status === 204 ? null : response.json();
    };
    const apiPost = async (endpoint, body) => {
        const response = await fetch(`${basePath}/api/${endpoint}`, {
            method: 'POST',
            credentials: 'same-origin',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(body)
        });
        if (response.status === 401) {
            redirectToLogin();
            throw new Error('Unauthorized');
        }
        return response;
    };
    const apiDelete = async (endpoint) => {
        const response = await fetch(`${basePath}/api/${endpoint}`, {
            method: 'DELETE',
            credentials: 'same-origin'
        });
        if (response.status === 401) {
            redirectToLogin();
            throw new Error('Unauthorized');
        }
        return response;
    };
    const $ = (sel) => root.querySelector(sel);
    const content = $('#content');

    // --- Component Router ---

    function navigate(route) {
        const nextRoute = normalizeRoute(route);
        const changed = nextRoute !== currentRoute;
        currentRoute = nextRoute;
        if (changed) {
            options.onNavigate?.(currentRoute);
        }
        handleRoute();
    }

    function handleRoute() {
        if (destroyed) {
            return;
        }

        closeModal();
        const route = currentRoute.slice(1);
        const slashIdx = route.indexOf('/');
        const view = slashIdx === -1 ? route : route.slice(0, slashIdx);
        const id = slashIdx === -1 ? null : decodeURIComponent(route.slice(slashIdx + 1));
        updateActiveNav(view);
        content.innerHTML = '<div class="loading">Loading...</div>';

        if (view === 'resources' && id) return loadResourceDetail(id);
        if (view === 'resources') return loadResources();
        if (view === 'users' && id) return loadSubjectDetail(id, 'users');
        if (view === 'users') return loadUsers();
        if (view === 'agents' && id) return loadSubjectDetail(id, 'agents');
        if (view === 'agents') return loadAgents();
        if (view === 'service-accounts' && id) return loadSubjectDetail(id, 'service-accounts');
        if (view === 'service-accounts') return loadServiceAccounts();
        if (view === 'user-groups' && id) return loadSubjectDetail(id, 'user-groups');
        if (view === 'user-groups') return loadUserGroups();
        if (view === 'grants') return loadGrants();
        if (view === 'roles' && id) return loadRoleDetail(id);
        if (view === 'roles') return loadRoles();
        if (view === 'permissions') return loadPermissions();
        if (view === 'access-tester') return loadAccessTester();
        return loadResources();
    }

    function updateActiveNav(view) {
        root.querySelectorAll('nav a').forEach(a => {
            a.classList.toggle('active', a.dataset.view === view);
        });
    }

    // --- Subject type to route mapping ---

    function subjectTypeToRoute(typeId) {
        const map = { 'user': 'users', 'agent': 'agents', 'service_account': 'service-accounts', 'group': 'user-groups', 'user_group': 'user-groups' };
        return map[typeId] || 'users';
    }

    // Load stats at mount and after mutations only. Never block list rendering.
    async function loadStats() {
        try {
            const stats = await api('stats');
            const cards = Object.entries(stats || {}).map(([key, value]) =>
                `<div class="stat-card"><div class="label">${esc(key)}</div><div class="value">${esc(String(value))}</div></div>`
            ).join('');
            $('#stats').innerHTML = cards;
        } catch {
            // Stats are advisory; list views render independently.
        }
    }

    // --- Cursor pagination (same contract as the auth dashboard) ---

    function createPager(filterKey, pageSize) {
        return { pageSize: pageSize || 25, cursors: [null], index: 0, filterKey: filterKey || '' };
    }

    function syncPagerFilter(pager, filterKey) {
        const nextKey = filterKey || '';
        if (pager.filterKey !== nextKey) {
            pager.cursors = [null];
            pager.index = 0;
            pager.filterKey = nextKey;
        }
        return pager;
    }

    function cursorQueryString(pager, extras) {
        const params = new URLSearchParams();
        params.set('pageSize', String(pager.pageSize || 25));
        const cursor = pager.cursors[pager.index];
        if (cursor) params.set('cursor', cursor);
        if (extras) {
            Object.entries(extras).forEach(([key, value]) => {
                if (value !== undefined && value !== null && value !== '') {
                    params.set(key, String(value));
                }
            });
        }
        return params.toString();
    }

    function pageItems(result) {
        return result && Array.isArray(result.data) ? result.data : [];
    }

    function renderPagination(pager, result) {
        const hasPrev = pager.index > 0;
        const hasNext = !!(result && result.hasNextPage && result.nextCursor);
        if (!hasPrev && !hasNext) return '';
        return `<div class="pagination">
            <button type="button" class="pg-btn" data-dir="prev" ${hasPrev ? '' : 'disabled'}>Previous</button>
            <button type="button" class="pg-btn" data-dir="next" ${hasNext ? '' : 'disabled'}>Next</button>
        </div>`;
    }

    function bindPagination(containerSel, pager, result, reload) {
        const container = $(containerSel);
        if (!container) return;
        container.querySelector('.pg-btn[data-dir="prev"]:not([disabled])')?.addEventListener('click', () => {
            if (pager.index > 0) {
                pager.index -= 1;
                reload();
            }
        });
        container.querySelector('.pg-btn[data-dir="next"]:not([disabled])')?.addEventListener('click', () => {
            if (result && result.hasNextPage && result.nextCursor) {
                pager.cursors = pager.cursors.slice(0, pager.index + 1);
                pager.cursors.push(result.nextCursor);
                pager.index += 1;
                reload();
            }
        });
    }

    function renderRemotePicker(id, placeholder) {
        return `<div class="remote-picker" id="${esc(id)}">
            <input type="hidden" class="remote-picker-value" value="">
            <input type="text" class="remote-picker-search" placeholder="${esc(placeholder)}" autocomplete="off">
            <div class="remote-picker-results" hidden></div>
        </div>`;
    }

    function bindRemotePicker(id, config) {
        const el = $(`#${id}`);
        if (!el) return { getValue: () => null };
        const searchInput = el.querySelector('.remote-picker-search');
        const resultsEl = el.querySelector('.remote-picker-results');
        const hidden = el.querySelector('.remote-picker-value');
        let items = [];
        let nextCursor = null;
        let hasNextPage = false;
        let loading = false;
        let debounceTimer = null;
        let selectedLabel = '';
        const pageSize = config.pageSize || 25;

        async function load({ append } = {}) {
            if (loading) return;
            loading = true;
            const params = new URLSearchParams();
            params.set('pageSize', String(pageSize));
            const query = searchInput.value.trim();
            if (query && query !== selectedLabel) params.set('search', query);
            if (append && nextCursor) params.set('cursor', nextCursor);
            try {
                const result = await api(`${config.endpoint}?${params.toString()}`);
                const data = pageItems(result);
                items = append ? items.concat(data) : data;
                nextCursor = result.nextCursor || null;
                hasNextPage = !!result.hasNextPage;
                renderResults();
            } catch (err) {
                resultsEl.innerHTML = `<div class="remote-picker-empty">Error: ${esc(err.message)}</div>`;
                resultsEl.hidden = false;
            }
            loading = false;
        }

        function renderResults() {
            const filter = config.filter || (() => true);
            const shown = items.filter(filter);
            let html = shown.map(item => {
                const value = String(config.getValue(item) ?? '');
                const label = config.getLabel(item) || value;
                const selected = hidden.value === value ? ' selected' : '';
                return `<button type="button" class="remote-picker-item${selected}" data-value="${esc(value)}">${esc(label)}</button>`;
            }).join('');
            if (shown.length === 0) {
                html += `<div class="remote-picker-empty">${hasNextPage ? 'No matches on this page' : 'No matches'}</div>`;
            }
            if (hasNextPage) html += '<button type="button" class="remote-picker-more">Load more</button>';
            resultsEl.innerHTML = html;
            resultsEl.hidden = false;
            resultsEl.querySelectorAll('.remote-picker-item').forEach(btn => {
                btn.addEventListener('click', () => {
                    hidden.value = btn.dataset.value;
                    selectedLabel = btn.textContent || '';
                    searchInput.value = selectedLabel;
                    resultsEl.hidden = true;
                    config.onChange?.(hidden.value);
                });
            });
            resultsEl.querySelector('.remote-picker-more')?.addEventListener('click', (e) => {
                e.preventDefault();
                e.stopPropagation();
                load({ append: true });
            });
        }

        searchInput.addEventListener('focus', () => {
            if (items.length === 0) load({ append: false });
            else resultsEl.hidden = false;
        });
        searchInput.addEventListener('input', () => {
            hidden.value = '';
            selectedLabel = '';
            clearTimeout(debounceTimer);
            debounceTimer = setTimeout(() => load({ append: false }), 300);
        });

        return {
            getValue: () => hidden.value || null,
            setValue(value, label) {
                hidden.value = value || '';
                selectedLabel = label || '';
                searchInput.value = label || '';
            }
        };
    }

    root.addEventListener('click', (e) => {
        root.querySelectorAll('.remote-picker').forEach(picker => {
            if (!picker.contains(e.target)) {
                const results = picker.querySelector('.remote-picker-results');
                if (results) results.hidden = true;
            }
        });
    });

    function renderSearchBox(id, placeholder) {
        return `<div class="search-box"><input type="text" placeholder="${placeholder}" id="${id}"></div>`;
    }

    function renderSeedNotice() {
        return `<div class="card" style="margin-bottom:1rem"><strong>Startup seed notice:</strong> Resource types, roles, and permissions defined in startup code are reapplied on boot. Custom roles and permissions created later are preserved.</div>`;
    }

    // --- Resource Tree (cursor-paginated roots, lazy children) ---

    let treeNodes = new Map();
    let treeRootIds = [];
    let treeRootsCursor = null;
    let treeRootsHasNext = false;
    let treeRootsLoading = false;

    async function loadResources() {
        try {
            const result = await api('resources/tree?pageSize=25');
            if (result.error) {
                content.innerHTML = `<div class="card"><p style="color:red">Error loading resources: ${esc(result.error)}</p></div>`;
                return;
            }
            treeNodes = new Map();
            treeRootIds = [];
            pageItems(result).forEach(n => {
                treeNodes.set(n.id, {
                    ...n,
                    expanded: false,
                    childrenLoaded: false,
                    childrenCursor: null,
                    hasMoreChildren: false,
                    isLoading: false
                });
                treeRootIds.push(n.id);
            });
            treeRootsCursor = result.nextCursor || null;
            treeRootsHasNext = !!result.hasNextPage;
            treeRootsLoading = false;
            renderResourceTree();
        } catch (err) {
            content.innerHTML = `<div class="card"><p style="color:red">Error loading resources: ${esc(err.message)}</p></div>`;
        }
    }

    async function loadMoreRoots() {
        if (!treeRootsHasNext || !treeRootsCursor || treeRootsLoading) return;
        treeRootsLoading = true;
        renderResourceTree();
        try {
            const result = await api(`resources/tree?pageSize=25&cursor=${encodeURIComponent(treeRootsCursor)}`);
            pageItems(result).forEach(n => {
                if (!treeNodes.has(n.id)) {
                    treeNodes.set(n.id, {
                        ...n,
                        expanded: false,
                        childrenLoaded: false,
                        childrenCursor: null,
                        hasMoreChildren: false,
                        isLoading: false
                    });
                    treeRootIds.push(n.id);
                }
            });
            treeRootsCursor = result.nextCursor || null;
            treeRootsHasNext = !!result.hasNextPage;
        } catch (err) {
            console.error('Failed to load more roots:', err);
        }
        treeRootsLoading = false;
        renderResourceTree();
    }

    function renderResourceTree() {
        content.innerHTML = `${renderSeedNotice()}<div class="card"><h3 style="margin-bottom:1rem">Resource Hierarchy</h3><div id="tree"></div></div>`;
        let html = treeRootIds.map(id => renderTreeNode(id)).join('');
        if (treeRootsLoading && treeRootIds.length === 0) {
            html += '<div class="tree-loading">Loading...</div>';
        } else if (treeRootsLoading) {
            html += '<div class="tree-loading">Loading more...</div>';
        } else if (treeRootsHasNext) {
            html += '<button type="button" class="tree-load-more" id="load-more-roots">Load more roots</button>';
        }
        $('#tree').innerHTML = html;
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
                    html += `<button class="tree-load-more" data-id="${esc(n.id)}">Load more</button>`;
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
        ids.sort((a, b) => (treeNodes.get(a).name || '').localeCompare(treeNodes.get(b).name || ''));
        return ids;
    }

    function bindTreeEvents() {
        root.querySelectorAll('.toggle[data-id]').forEach(el => {
            el.addEventListener('click', (e) => { e.stopPropagation(); handleToggle(el.dataset.id); });
        });
        root.querySelectorAll('.tree-resource-name[data-id]').forEach(el => {
            el.addEventListener('click', (e) => { e.stopPropagation(); navigate('#/resources/' + encodeURIComponent(el.dataset.id)); });
        });
        root.querySelectorAll('.tree-load-more[data-id]').forEach(el => {
            el.addEventListener('click', () => handleLoadMore(el.dataset.id));
        });
        $('#load-more-roots')?.addEventListener('click', () => loadMoreRoots());
        root.querySelectorAll('.grants-badge[data-resource-id]').forEach(el => {
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

    root.addEventListener('click', (e) => {
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

        await loadGrantsPopupPage(resourceId, createPager('', 10));
    }

    async function loadGrantsPopupPage(resourceId, pager) {
        if (!activeGrantsPopup) return;

        const contentEl = activeGrantsPopup.querySelector('.grants-popup-content');
        contentEl.innerHTML = '<div class="grants-popup-loading">Loading...</div>';

        try {
            const result = await api(`resources/${encodeURIComponent(resourceId)}/grants?${cursorQueryString(pager)}`);

            if (!activeGrantsPopup) return;

            const rows = pageItems(result);
            if (rows.length === 0 && pager.index === 0) {
                contentEl.innerHTML = '<div class="grants-popup-empty">No direct grants on this resource</div>';
                const emptyBar = activeGrantsPopup.querySelector('.grants-popup-pagination-bar');
                if (emptyBar) emptyBar.remove();
                return;
            }

            let html = `<table class="grants-popup-table">
                <thead><tr><th>Subject</th><th>Type</th><th>Role</th></tr></thead>
                <tbody>`;
            rows.forEach(g => {
                html += `<tr>
                    <td><span class="grants-popup-subject" title="${esc(g.subjectId)}">${esc(g.subjectName)}</span></td>
                    <td><span class="grants-popup-subject-type">${esc(g.subjectType || '-')}</span></td>
                    <td><span class="grants-popup-role">${esc(g.roleName)}</span></td>
                </tr>`;
            });
            html += '</tbody></table>';
            contentEl.innerHTML = html;

            let paginationEl = activeGrantsPopup.querySelector('.grants-popup-pagination-bar');
            if (!paginationEl) {
                paginationEl = document.createElement('div');
                paginationEl.className = 'grants-popup-pagination-bar';
                const headerEl = activeGrantsPopup.querySelector('.grants-popup-header');
                headerEl.parentNode.insertBefore(paginationEl, headerEl.nextSibling);
            }
            const hasPrev = pager.index > 0;
            const hasNext = !!(result.hasNextPage && result.nextCursor);
            paginationEl.innerHTML = `
                <div class="grants-popup-pagination">
                    <button type="button" class="grants-popup-prev" ${hasPrev ? '' : 'disabled'}>&laquo;</button>
                    <button type="button" class="grants-popup-next" ${hasNext ? '' : 'disabled'}>&raquo;</button>
                </div>
            `;
            paginationEl.querySelector('.grants-popup-prev')?.addEventListener('click', (e) => {
                e.stopPropagation();
                if (pager.index > 0) {
                    pager.index -= 1;
                    loadGrantsPopupPage(resourceId, pager);
                }
            });
            paginationEl.querySelector('.grants-popup-next')?.addEventListener('click', (e) => {
                e.stopPropagation();
                if (result.hasNextPage && result.nextCursor) {
                    pager.cursors = pager.cursors.slice(0, pager.index + 1);
                    pager.cursors.push(result.nextCursor);
                    pager.index += 1;
                    loadGrantsPopupPage(resourceId, pager);
                }
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
        $('#back-to-resources').addEventListener('click', () => navigate('#/resources'));
        $('#add-grant-resource-btn').addEventListener('click', () => openGrantModal(null, r.id));
    }

    async function handleToggle(nodeId) {
        const node = treeNodes.get(nodeId);
        if (!node || node.childCount === 0) return;

        node.expanded = !node.expanded;

        if (node.expanded && !node.childrenLoaded) {
            node.isLoading = true;
            renderResourceTree();
            try {
                const result = await api(`resources/${encodeURIComponent(nodeId)}/children?pageSize=25`);
                pageItems(result).forEach(child => {
                    if (!treeNodes.has(child.id)) {
                        treeNodes.set(child.id, {
                            ...child,
                            expanded: false,
                            childrenLoaded: false,
                            childrenCursor: null,
                            hasMoreChildren: false,
                            isLoading: false
                        });
                    }
                });
                node.childrenLoaded = true;
                node.childrenCursor = result.nextCursor || null;
                node.hasMoreChildren = !!result.hasNextPage;
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
            const cursor = node.childrenCursor;
            const query = cursor
                ? `pageSize=25&cursor=${encodeURIComponent(cursor)}`
                : 'pageSize=25';
            const result = await api(`resources/${encodeURIComponent(nodeId)}/children?${query}`);
            pageItems(result).forEach(child => {
                if (!treeNodes.has(child.id)) {
                    treeNodes.set(child.id, {
                        ...child,
                        expanded: false,
                        childrenLoaded: false,
                        childrenCursor: null,
                        hasMoreChildren: false,
                        isLoading: false
                    });
                }
            });
            node.childrenCursor = result.nextCursor || null;
            node.hasMoreChildren = !!result.hasNextPage;
        } catch (e) {
            console.error('Failed to load more children:', e);
        }
        node.isLoading = false;
        renderResourceTree();
    }

    // --- Paginated table views ---

    async function loadUsers(opts = {}) {
        const search = opts.search || '';
        const pager = opts.pager || createPager(search);
        syncPagerFilter(pager, search);
        const result = await api(`users?${cursorQueryString(pager, { search })}`);
        const rows = pageItems(result);

        content.innerHTML = `<div class="card">
            ${renderSearchBox('users-search', 'Search users...')}
            <table><thead><tr><th>Display Name</th><th>Email</th><th>Active</th><th>Created</th></tr></thead>
            <tbody>${rows.map(u => `<tr class="subject-row" data-id="${esc(u.subjectId)}" style="cursor:pointer">
                <td>${esc(u.displayName)}</td><td>${esc(u.email || '-')}</td>
                <td>${u.isActive ? 'Yes' : 'No'}</td>
                <td>${new Date(u.createdAt).toLocaleDateString()}</td>
            </tr>`).join('')}</tbody></table>
            <div id="users-pagination">${renderPagination(pager, result)}</div>
        </div>`;

        root.querySelectorAll('.subject-row').forEach(row => {
            row.addEventListener('click', () => navigate('#/users/' + encodeURIComponent(row.dataset.id)));
        });
        bindPagination('#users-pagination', pager, result, () => loadUsers({ pager, search }));
        const searchInput = $('#users-search');
        if (searchInput) {
            searchInput.value = search;
            let debounce;
            searchInput.addEventListener('input', (e) => {
                clearTimeout(debounce);
                debounce = setTimeout(() => loadUsers({ search: e.target.value }), 300);
            });
        }
    }

    async function loadAgents(opts = {}) {
        const search = opts.search || '';
        const pager = opts.pager || createPager(search);
        syncPagerFilter(pager, search);
        const result = await api(`agents?${cursorQueryString(pager, { search })}`);
        const rows = pageItems(result);

        content.innerHTML = `<div class="card">
            ${renderSearchBox('agents-search', 'Search agents...')}
            <table><thead><tr><th>Display Name</th><th>Type</th><th>Description</th><th>Created</th></tr></thead>
            <tbody>${rows.map(a => `<tr class="subject-row" data-id="${esc(a.subjectId)}" style="cursor:pointer">
                <td>${esc(a.displayName)}</td><td>${esc(a.agentType || '-')}</td>
                <td>${esc((a.description || '').slice(0, 50))}${(a.description || '').length > 50 ? '...' : ''}</td>
                <td>${new Date(a.createdAt).toLocaleDateString()}</td>
            </tr>`).join('')}</tbody></table>
            <div id="agents-pagination">${renderPagination(pager, result)}</div>
        </div>`;

        root.querySelectorAll('.subject-row').forEach(row => {
            row.addEventListener('click', () => navigate('#/agents/' + encodeURIComponent(row.dataset.id)));
        });
        bindPagination('#agents-pagination', pager, result, () => loadAgents({ pager, search }));
        const searchInput = $('#agents-search');
        if (searchInput) {
            searchInput.value = search;
            let debounce;
            searchInput.addEventListener('input', (e) => {
                clearTimeout(debounce);
                debounce = setTimeout(() => loadAgents({ search: e.target.value }), 300);
            });
        }
    }

    async function loadServiceAccounts(opts = {}) {
        const search = opts.search || '';
        const pager = opts.pager || createPager(search);
        syncPagerFilter(pager, search);
        const result = await api(`service-accounts?${cursorQueryString(pager, { search })}`);
        const rows = pageItems(result);

        content.innerHTML = `<div class="card">
            ${renderSearchBox('sa-search', 'Search service accounts...')}
            <table><thead><tr><th>Display Name</th><th>Client ID</th><th>Description</th><th>Created</th></tr></thead>
            <tbody>${rows.map(s => `<tr class="subject-row" data-id="${esc(s.subjectId)}" style="cursor:pointer">
                <td>${esc(s.displayName)}</td><td><code>${esc(s.clientId)}</code></td>
                <td>${esc((s.description || '').slice(0, 40))}${(s.description || '').length > 40 ? '...' : ''}</td>
                <td>${new Date(s.createdAt).toLocaleDateString()}</td>
            </tr>`).join('')}</tbody></table>
            <div id="sa-pagination">${renderPagination(pager, result)}</div>
        </div>`;

        root.querySelectorAll('.subject-row').forEach(row => {
            row.addEventListener('click', () => navigate('#/service-accounts/' + encodeURIComponent(row.dataset.id)));
        });
        bindPagination('#sa-pagination', pager, result, () => loadServiceAccounts({ pager, search }));
        const searchInput = $('#sa-search');
        if (searchInput) {
            searchInput.value = search;
            let debounce;
            searchInput.addEventListener('input', (e) => {
                clearTimeout(debounce);
                debounce = setTimeout(() => loadServiceAccounts({ search: e.target.value }), 300);
            });
        }
    }

    async function loadUserGroups(opts = {}) {
        const search = opts.search || '';
        const pager = opts.pager || createPager(search);
        syncPagerFilter(pager, search);
        const result = await api(`user-groups?${cursorQueryString(pager, { search })}`);
        const rows = pageItems(result);

        content.innerHTML = `<div class="card">
            ${renderSearchBox('groups-search', 'Search groups...')}
            <table><thead><tr><th>Name</th><th>Type</th><th>Members</th><th>Created</th></tr></thead>
            <tbody>${rows.map(g => `<tr class="subject-row" data-id="${esc(g.subjectId)}" style="cursor:pointer">
                <td>${esc(g.name)}</td><td>${esc(g.groupType || '-')}</td>
                <td><span class="badge badge-gray">${g.memberCount}</span></td>
                <td>${new Date(g.createdAt).toLocaleDateString()}</td>
            </tr>`).join('')}</tbody></table>
            <div id="groups-pagination">${renderPagination(pager, result)}</div>
        </div>`;

        root.querySelectorAll('.subject-row').forEach(row => {
            row.addEventListener('click', () => navigate('#/user-groups/' + encodeURIComponent(row.dataset.id)));
        });
        bindPagination('#groups-pagination', pager, result, () => loadUserGroups({ pager, search }));
        const searchInput = $('#groups-search');
        if (searchInput) {
            searchInput.value = search;
            let debounce;
            searchInput.addEventListener('input', (e) => {
                clearTimeout(debounce);
                debounce = setTimeout(() => loadUserGroups({ search: e.target.value }), 300);
            });
        }
    }

    // --- Subject Detail ---

    async function loadSubjectDetail(subjectId, backView) {
        backView = backView || 'users';
        content.innerHTML = '<div class="loading">Loading...</div>';
        const [detail, grantsResult] = await Promise.all([
            api(`subjects/${encodeURIComponent(subjectId)}`),
            api(`subjects/${encodeURIComponent(subjectId)}/grants?pageSize=25`)
        ]);
        const grantsState = {
            rows: pageItems(grantsResult),
            nextCursor: grantsResult.nextCursor || null,
            hasNextPage: !!grantsResult.hasNextPage
        };
        renderSubjectDetail(detail, grantsState, backView);
    }

    function renderSubjectDetail(detail, grantsState, backView) {
        backView = backView || 'users';
        const s = detail.subject;
        const backLabels = { 'users': 'Users', 'agents': 'Agents', 'service-accounts': 'Service Accounts', 'user-groups': 'User Groups' };
        const backLabel = backLabels[backView] || 'Users';
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
                <tbody>${detail.members.map(m => `<tr class="member-row" data-id="${esc(m.id)}" data-type="${esc(m.subjectTypeId)}" style="cursor:pointer">
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
            ${renderSubjectGrantsTable(grantsState, s.id)}
        </div>`;

        content.innerHTML = html;
        bindSubjectDetailEvents(s.id, backView, grantsState);
    }

    function renderSubjectGrantsTable(grantsState, subjectId) {
        if (!grantsState.rows.length) {
            return '<p style="color:#888;font-size:0.9rem">No grants found for this subject.</p>';
        }
        let html = `<table><thead><tr>
            <th>Role</th><th>Resource</th><th>Effective From</th><th>Effective To</th><th>Created</th><th></th>
        </tr></thead><tbody>`;
        grantsState.rows.forEach(g => {
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
        if (grantsState.hasNextPage && grantsState.nextCursor) {
            html += `<div class="load-more-wrap"><button type="button" class="tree-load-more" id="subject-grants-more">Load more</button></div>`;
        }
        return html;
    }

    function bindSubjectDetailEvents(subjectId, backView, grantsState, bindChrome) {
        backView = backView || 'users';
        if (bindChrome !== false) {
            $('#back-to-subjects').addEventListener('click', () => navigate('#/' + backView));
            root.querySelectorAll('[data-group-subject]').forEach(el => {
                el.addEventListener('click', () => navigate('#/user-groups/' + encodeURIComponent(el.dataset.groupSubject)));
            });
            root.querySelectorAll('.member-row').forEach(row => {
                const route = subjectTypeToRoute(row.dataset.type);
                row.addEventListener('click', () => navigate('#/' + route + '/' + encodeURIComponent(row.dataset.id)));
            });
        }
        $('#grant-role-btn')?.addEventListener('click', () => openGrantModal(subjectId, null));
        root.querySelectorAll('.revoke-btn').forEach(btn => {
            btn.addEventListener('click', async (e) => {
                e.stopPropagation();
                const grantId = btn.dataset.grantId;
                if (!confirm('Revoke this grant?')) return;
                try {
                    const resp = await apiDelete(`grants/${grantId}`);
                    if (!resp.ok) throw new Error('Failed to revoke');
                    loadStats();
                    const r = await api(`subjects/${encodeURIComponent(subjectId)}/grants?pageSize=25`);
                    grantsState.rows = pageItems(r);
                    grantsState.nextCursor = r.nextCursor || null;
                    grantsState.hasNextPage = !!r.hasNextPage;
                    const card = root.querySelector('#grant-role-btn')?.closest('.card');
                    if (card) card.innerHTML = '<h3 style="margin-bottom:1rem;display:flex;align-items:center;gap:1rem">Role Grants<button class="btn-primary btn-sm" id="grant-role-btn">Grant Role</button></h3>' + renderSubjectGrantsTable(grantsState, subjectId);
                    bindSubjectDetailEvents(subjectId, backView, grantsState, false);
                } catch (err) {
                    alert('Error: ' + (err.message || 'Unknown error'));
                }
            });
        });
        $('#subject-grants-more')?.addEventListener('click', async () => {
            if (!grantsState.nextCursor) return;
            const more = await api(`subjects/${encodeURIComponent(subjectId)}/grants?pageSize=25&cursor=${encodeURIComponent(grantsState.nextCursor)}`);
            grantsState.rows = grantsState.rows.concat(pageItems(more));
            grantsState.nextCursor = more.nextCursor || null;
            grantsState.hasNextPage = !!more.hasNextPage;
            const card = root.querySelector('#grant-role-btn')?.closest('.card');
            if (card) card.innerHTML = '<h3 style="margin-bottom:1rem;display:flex;align-items:center;gap:1rem">Role Grants<button class="btn-primary btn-sm" id="grant-role-btn">Grant Role</button></h3>' + renderSubjectGrantsTable(grantsState, subjectId);
            bindSubjectDetailEvents(subjectId, backView, grantsState, false);
        });
    }

    async function loadGrants(opts = {}) {
        const search = opts.search || '';
        const pager = opts.pager || createPager(search);
        syncPagerFilter(pager, search);
        const result = await api(`grants?${cursorQueryString(pager, { search })}`);
        const rows = pageItems(result);

        content.innerHTML = `${renderSeedNotice()}<div class="card">
            <div style="display:flex;align-items:center;gap:1rem;margin-bottom:1rem;flex-wrap:wrap">
                <div style="flex:1;min-width:200px">${renderSearchBox('grants-search', 'Search grants...')}</div>
                <button class="btn-primary btn-sm" id="add-grant-btn">Add Grant</button>
            </div>
            <table><thead><tr>
                <th>Subject</th><th>Role</th><th>Resource</th><th>Effective From</th><th>Effective To</th><th>Created</th><th></th>
            </tr></thead><tbody>${rows.map(g => `<tr>
                <td>${esc(g.subjectName)}</td><td><span class="badge badge-blue">${esc(g.roleName)}</span></td>
                <td>${esc(g.resourceName)}</td>
                <td>${g.effectiveFrom ? new Date(g.effectiveFrom).toLocaleDateString() : '-'}</td>
                <td>${g.effectiveTo ? new Date(g.effectiveTo).toLocaleDateString() : '-'}</td>
                <td>${new Date(g.createdAt).toLocaleDateString()}</td>
                <td><button class="btn-danger btn-sm revoke-btn grant-revoke-btn" data-grant-id="${esc(g.id)}">Revoke</button></td>
            </tr>`).join('')}</tbody></table>
            <div id="grants-pagination">${renderPagination(pager, result)}</div>
        </div>`;

        $('#add-grant-btn')?.addEventListener('click', () => openGrantModal(null, null));
        root.querySelectorAll('.grant-revoke-btn').forEach(btn => {
            btn.addEventListener('click', async (e) => {
                e.stopPropagation();
                const grantId = btn.dataset.grantId;
                if (!confirm('Revoke this grant?')) return;
                try {
                    const resp = await apiDelete(`grants/${grantId}`);
                    if (!resp.ok) throw new Error('Failed to revoke');
                    loadStats();
                    loadGrants({ pager, search: $('#grants-search')?.value ?? search });
                } catch (err) {
                    alert('Error: ' + (err.message || 'Unknown error'));
                }
            });
        });
        bindPagination('#grants-pagination', pager, result, () => loadGrants({ pager, search }));
        const searchInput = $('#grants-search');
        if (searchInput) {
            searchInput.value = search;
            let debounce;
            searchInput.addEventListener('input', (e) => {
                clearTimeout(debounce);
                debounce = setTimeout(() => loadGrants({ search: e.target.value }), 300);
            });
        }
    }

    async function loadRoles(opts = {}) {
        const search = opts.search || '';
        const pager = opts.pager || createPager(search);
        syncPagerFilter(pager, search);
        const result = await api(`roles?${cursorQueryString(pager, { search })}`);
        const rows = pageItems(result);

        content.innerHTML = `${renderSeedNotice()}<div class="card">
            <div style="display:flex;align-items:center;gap:1rem;margin-bottom:1rem;flex-wrap:wrap">
                <div style="flex:1;min-width:200px">${renderSearchBox('roles-search', 'Search roles...')}</div>
                <button class="btn-primary btn-sm" id="create-role-btn">Create Role</button>
            </div>
            <table><thead><tr>
                <th>Name</th><th>Key</th><th>Permissions</th><th>Virtual</th>
            </tr></thead><tbody>${rows.map(r => `<tr class="role-row" data-id="${esc(r.id)}" style="cursor:pointer">
                <td>${esc(r.name)}</td><td><code>${esc(r.key)}</code></td>
                <td><span class="badge badge-gray">${r.permissionCount}</span></td>
                <td>${r.isVirtual ? 'Yes' : 'No'}</td>
            </tr>`).join('')}</tbody></table>
            <div id="roles-pagination">${renderPagination(pager, result)}</div>
        </div>`;

        $('#create-role-btn')?.addEventListener('click', openCreateRoleModal);
        root.querySelectorAll('.role-row').forEach(row => {
            row.addEventListener('click', () => navigate('#/roles/' + encodeURIComponent(row.dataset.id)));
        });
        bindPagination('#roles-pagination', pager, result, () => loadRoles({ pager, search }));
        const searchInput = $('#roles-search');
        if (searchInput) {
            searchInput.value = search;
            let debounce;
            searchInput.addEventListener('input', (e) => {
                clearTimeout(debounce);
                debounce = setTimeout(() => loadRoles({ search: e.target.value }), 300);
            });
        }
    }

    async function loadRoleDetail(roleId) {
        content.innerHTML = '<div class="loading">Loading...</div>';
        const [role, perms] = await Promise.all([
            api(`roles/${encodeURIComponent(roleId)}`),
            api(`roles/${encodeURIComponent(roleId)}/permissions`)
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

        $('#back-to-roles').addEventListener('click', () => navigate('#/roles'));
        $('#delete-role-btn').addEventListener('click', async () => {
            if (!confirm('Delete this role? This cannot be undone.')) return;
            try {
                const resp = await apiDelete(`roles/${encodeURIComponent(roleId)}`);
                if (!resp.ok) {
                    const err = await resp.json().catch(() => ({}));
                    throw new Error(err.error || 'Failed to delete role');
                }
                loadStats();
                navigate('#/roles');
            } catch (e) {
                alert('Error: ' + (e.message || 'Unknown error'));
            }
        });
        $('#add-perm-btn').addEventListener('click', () => openAddPermissionToRoleModal(roleId, perms));
        root.querySelectorAll('.remove-perm-btn').forEach(btn => {
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
        $('#modal-overlay').hidden = false;

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
                navigate('#/roles');
            } catch (e) {
                alert('Error: ' + (e.message || 'Unknown error'));
            }
        });
    }

    function openAddPermissionToRoleModal(roleId, existingPerms) {
        const existingIds = new Set(existingPerms.map(p => p.id));

        let html = `<h3>Add Permission to Role</h3>
            <div class="modal-field">
                <label>Permission</label>
                ${renderRemotePicker('add-perm-picker', 'Search permissions...')}
            </div>
            <div class="modal-actions">
                <button class="btn-secondary" id="add-perm-cancel">Cancel</button>
                <button class="btn-primary" id="add-perm-submit">Add</button>
            </div>`;

        $('#modal').innerHTML = html;
        $('#modal-overlay').hidden = false;

        const picker = bindRemotePicker('add-perm-picker', {
            endpoint: 'permissions',
            pageSize: 25,
            getValue: p => p.id,
            getLabel: p => `${p.key} - ${p.name}`,
            filter: p => !existingIds.has(p.id)
        });

        $('#add-perm-cancel').addEventListener('click', closeModal);
        $('#modal-overlay').onclick = (e) => { if (e.target === $('#modal-overlay')) closeModal(); };

        $('#add-perm-submit').addEventListener('click', async () => {
            const permId = picker.getValue();
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

    async function loadPermissions(opts = {}) {
        const search = opts.search || '';
        const pager = opts.pager || createPager(search);
        syncPagerFilter(pager, search);
        const result = await api(`permissions?${cursorQueryString(pager, { search })}`);
        const rows = pageItems(result);

        content.innerHTML = `${renderSeedNotice()}<div class="card">
            <div style="display:flex;align-items:center;gap:1rem;margin-bottom:1rem;flex-wrap:wrap">
                <div style="flex:1;min-width:200px">${renderSearchBox('perm-search', 'Search permissions...')}</div>
                <button class="btn-primary btn-sm" id="create-perm-btn">Create Permission</button>
            </div>
            <table><thead><tr><th>Key</th><th>Name</th><th>Resource Type</th></tr></thead>
            <tbody>${rows.map(p => `<tr>
                <td><code>${esc(p.key)}</code></td><td>${esc(p.name)}</td>
                <td>${p.resourceType ? `<span class="badge badge-green">${esc(p.resourceType)}</span>` : '-'}</td>
            </tr>`).join('')}</tbody></table>
            <div id="perms-pagination">${renderPagination(pager, result)}</div>
        </div>`;

        $('#create-perm-btn')?.addEventListener('click', () => openCreatePermissionModal());
        bindPagination('#perms-pagination', pager, result, () => loadPermissions({ pager, search }));
        const searchInput = $('#perm-search');
        if (searchInput) {
            searchInput.value = search;
            let debounce;
            searchInput.addEventListener('input', (e) => {
                clearTimeout(debounce);
                debounce = setTimeout(() => loadPermissions({ search: e.target.value }), 300);
            });
        }
    }

    function openCreatePermissionModal() {
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
                ${renderRemotePicker('perm-resource-type-picker', 'Search resource types...')}
            </div>
            <div class="modal-actions">
                <button class="btn-secondary" id="perm-cancel">Cancel</button>
                <button class="btn-primary" id="perm-submit">Create</button>
            </div>`;

        $('#modal').innerHTML = html;
        $('#modal-overlay').hidden = false;

        const typePicker = bindRemotePicker('perm-resource-type-picker', {
            endpoint: 'resource-types',
            pageSize: 25,
            getValue: rt => rt.id,
            getLabel: rt => `${rt.name} (${rt.key})`
        });

        $('#perm-cancel').addEventListener('click', closeModal);
        $('#modal-overlay').onclick = (e) => { if (e.target === $('#modal-overlay')) closeModal(); };

        $('#perm-submit').addEventListener('click', async () => {
            const key = $('#perm-key').value.trim();
            const name = $('#perm-name').value.trim();
            const desc = $('#perm-desc').value.trim();
            const resourceTypeId = typePicker.getValue();
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
                handleRoute();
            } catch (e) {
                alert('Error: ' + (e.message || 'Unknown error'));
            }
        });
    }

    // --- Access Tester ---

    let accessTesterMode = 'check';
    let testerPickers = {};

    async function loadAccessTester() {
        content.innerHTML = `<div class="card">
            <h3 style="margin-bottom:1rem">Access Tester</h3>
            <div class="tabs" style="margin-bottom:1rem">
                <button class="tester-mode-btn ${accessTesterMode === 'check' ? 'active' : ''}" data-mode="check">Check Access</button>
                <button class="tester-mode-btn ${accessTesterMode === 'list' ? 'active' : ''}" data-mode="list">List Access</button>
            </div>
            <div id="tester-content"></div>
        </div>`;

        root.querySelectorAll('.tester-mode-btn').forEach(btn => {
            btn.addEventListener('click', () => {
                accessTesterMode = btn.dataset.mode;
                loadAccessTester();
            });
        });

        if (accessTesterMode === 'check') {
            renderCheckAccessForm();
        } else {
            renderListAccessForm();
        }
    }

    function renderCheckAccessForm() {
        $('#tester-content').innerHTML = `
            <p style="color:#666;margin-bottom:1.5rem;font-size:0.9rem">
                Test whether a subject has a specific permission on a resource. Returns a detailed trace of how the decision was made.
            </p>
            <div class="tester-form">
                <div class="tester-field">
                    <label>Subject</label>
                    ${renderRemotePicker('tester-subject-picker', 'Search subjects...')}
                </div>
                <div class="tester-field">
                    <label>Permission</label>
                    ${renderRemotePicker('tester-permission-picker', 'Search permissions...')}
                </div>
                <div class="tester-field">
                    <label>Resource</label>
                    ${renderRemotePicker('tester-resource-picker', 'Search resources...')}
                </div>
                <button id="tester-run" class="tester-btn">Test Access</button>
            </div>
            <div id="tester-result"></div>`;

        testerPickers = {
            subject: bindRemotePicker('tester-subject-picker', {
                endpoint: 'subjects',
                pageSize: 25,
                getValue: s => s.id,
                getLabel: s => `${s.displayName} (${s.subjectTypeId})`
            }),
            permission: bindRemotePicker('tester-permission-picker', {
                endpoint: 'permissions',
                pageSize: 25,
                getValue: p => p.key,
                getLabel: p => `${p.key} — ${p.name}`
            }),
            resource: bindRemotePicker('tester-resource-picker', {
                endpoint: 'resources',
                pageSize: 25,
                getValue: r => r.id,
                getLabel: r => `${r.name} (${r.resourceType})`
            })
        };

        $('#tester-run').addEventListener('click', runAccessTest);
    }

    function renderListAccessForm() {
        $('#tester-content').innerHTML = `
            <p style="color:#666;margin-bottom:1.5rem;font-size:0.9rem">
                List all subjects who have access to a specific resource (including inherited access).
            </p>
            <div class="tester-form" style="grid-template-columns:1fr auto">
                <div class="tester-field">
                    <label>Resource</label>
                    ${renderRemotePicker('list-resource-picker', 'Search resources...')}
                </div>
                <button id="list-run" class="tester-btn">List Access</button>
            </div>
            <div id="list-result"></div>`;

        testerPickers = {
            listResource: bindRemotePicker('list-resource-picker', {
                endpoint: 'resources',
                pageSize: 25,
                getValue: r => r.id,
                getLabel: r => `${r.name} (${r.resourceType})`
            })
        };

        $('#list-run').addEventListener('click', runListAccess);
    }

    async function runListAccess() {
        const resourceId = testerPickers.listResource?.getValue();
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
        const subjectId = testerPickers.subject?.getValue();
        const permissionKey = testerPickers.permission?.getValue();
        const resourceId = testerPickers.resource?.getValue();

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
        $('#modal-overlay').hidden = true;
    }

    async function openGrantModal(subjectId, resourceId) {
        let html = `<h3>${subjectId ? 'Grant Role to Subject' : resourceId ? 'Add Grant to Resource' : 'Create Grant'}</h3>`;
        if (!subjectId) {
            html += `<div class="modal-field">
                <label>Subject</label>
                ${renderRemotePicker('grant-subject-picker', 'Search subjects...')}
            </div>`;
        }
        html += `<div class="modal-field">
            <label>Role</label>
            ${renderRemotePicker('grant-role-picker', 'Search roles...')}
        </div>`;
        if (!resourceId) {
            html += `<div class="modal-field">
                <label>Resource</label>
                ${renderRemotePicker('grant-resource-picker', 'Search resources...')}
            </div>`;
        }
        html += `<div class="modal-actions">
            <button class="btn-secondary" id="grant-cancel">Cancel</button>
            <button class="btn-primary" id="grant-submit">Grant</button>
        </div>`;

        $('#modal').innerHTML = html;
        $('#modal-overlay').hidden = false;

        const subjectPicker = subjectId ? null : bindRemotePicker('grant-subject-picker', {
            endpoint: 'subjects',
            pageSize: 25,
            getValue: s => s.id,
            getLabel: s => `${s.displayName} (${s.subjectTypeId})`
        });
        const rolePicker = bindRemotePicker('grant-role-picker', {
            endpoint: 'roles',
            pageSize: 25,
            getValue: r => r.id,
            getLabel: r => `${r.name} (${r.key})`
        });
        const resourcePicker = resourceId ? null : bindRemotePicker('grant-resource-picker', {
            endpoint: 'resources',
            pageSize: 25,
            getValue: r => r.id,
            getLabel: r => `${r.name} (${r.resourceType})`
        });

        $('#grant-cancel').addEventListener('click', closeModal);
        $('#modal-overlay').onclick = (e) => { if (e.target === $('#modal-overlay')) closeModal(); };

        $('#grant-submit').addEventListener('click', async () => {
            const subj = subjectId || subjectPicker?.getValue();
            const role = rolePicker.getValue();
            const res = resourceId || resourcePicker?.getValue();
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
                handleRoute();
            } catch (e) {
                alert('Error: ' + (e.message || 'Unknown error'));
            }
        });
    }

    // Init
    loadStats();
    handleRoute();

        return {
            navigate,
            destroy() {
                destroyed = true;
                root.replaceChildren();
            }
        };
    }

    window.SqlOSFgaDashboard = Object.freeze({ mount });
})();
