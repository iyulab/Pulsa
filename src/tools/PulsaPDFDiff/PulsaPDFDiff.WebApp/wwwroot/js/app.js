(function () {
    // State
    let refFile = null, tgtFile = null;
    let refSession = null, tgtSession = null; // { id, pageCount }
    let currentPromptContent = '';
    let isComparing = false;

    // DOM refs
    const refZone = document.getElementById('refZone');
    const tgtZone = document.getElementById('tgtZone');
    const refInput = document.getElementById('refInput');
    const tgtInput = document.getElementById('tgtInput');
    const refName = document.getElementById('refName');
    const tgtName = document.getElementById('tgtName');
    const promptSelect = document.getElementById('promptSelect');
    const editPromptBtn = document.getElementById('editPromptBtn');
    const promptEditor = document.getElementById('promptEditor');
    const compareBtn = document.getElementById('compareBtn');
    const reportSection = document.getElementById('reportSection');
    const reportContent = document.getElementById('reportContent');
    const settingsBtn = document.getElementById('settingsBtn');
    const settingsModal = document.getElementById('settingsModal');
    const apiKeyInput = document.getElementById('apiKeyInput');
    const modelSelect = document.getElementById('modelSelect');
    const modelInput = document.getElementById('modelInput');
    const saveSettingsBtn = document.getElementById('saveSettingsBtn');
    const closeSettingsBtn = document.getElementById('closeSettingsBtn');
    const mappingSection = document.getElementById('mappingSection');
    const mappingBody = document.getElementById('mappingBody');
    const mappingHint = document.getElementById('mappingHint');
    const mappingTable = document.getElementById('mappingTable');
    const addMappingBtn = document.getElementById('addMappingBtn');
    const progressContainer = document.getElementById('progressContainer');
    const progressText = document.getElementById('progressText');
    const progressTokens = document.getElementById('progressTokens');
    const progressFill = document.getElementById('progressFill');
    const tokenSummary = document.getElementById('tokenSummary');
    const tokenBody = document.getElementById('tokenBody');
    const tokenFoot = document.getElementById('tokenFoot');

    // === File Upload ===

    function setupDropZone(zone, input, nameEl, onFile) {
        const hintEl = zone.querySelector('.drop-hint');
        async function applyFile(file) {
            zone.classList.add('has-file');
            if (hintEl) hintEl.textContent = '클릭하여 파일 변경';
            try {
                await onFile(file);
            } catch (e) {
                nameEl.textContent = `오류: ${e.message}`;
            }
        }
        zone.addEventListener('click', () => input.click());
        zone.addEventListener('dragover', (e) => { e.preventDefault(); zone.classList.add('dragover'); });
        zone.addEventListener('dragleave', () => zone.classList.remove('dragover'));
        zone.addEventListener('drop', (e) => {
            e.preventDefault();
            zone.classList.remove('dragover');
            const file = e.dataTransfer.files[0];
            if (file && file.type === 'application/pdf') applyFile(file);
        });
        input.addEventListener('change', () => {
            const file = input.files[0];
            if (file) applyFile(file);
        });
    }

    async function uploadFile(file) {
        const fd = new FormData();
        fd.append('file', file);
        const res = await fetch('/api/upload', { method: 'POST', body: fd });
        if (!res.ok) {
            const err = await res.json().catch(() => ({ error: res.statusText }));
            throw new Error(err.error || 'Upload failed');
        }
        return await res.json(); // { id, pageCount }
    }

    async function onRefFile(file) {
        refFile = file;
        refSession = null;
        try {
            nameEl(refName, '업로드 중...');
            refSession = await uploadFile(file);
            nameEl(refName, `${file.name} (${refSession.pageCount}p)`);
        } catch (e) {
            nameEl(refName, `오류: ${e.message}`);
        }
        updateMappingTable();
        updateCompareBtn();
    }

    async function onTgtFile(file) {
        tgtFile = file;
        tgtSession = null;
        try {
            nameEl(tgtName, '업로드 중...');
            tgtSession = await uploadFile(file);
            nameEl(tgtName, `${file.name} (${tgtSession.pageCount}p)`);
        } catch (e) {
            nameEl(tgtName, `오류: ${e.message}`);
        }
        updateMappingTable();
        updateCompareBtn();
    }

    function nameEl(el, text) { el.textContent = text; }

    setupDropZone(refZone, refInput, refName, onRefFile);
    setupDropZone(tgtZone, tgtInput, tgtName, onTgtFile);

    // === Mapping Table ===

    function updateMappingTable() {
        if (!refSession || !tgtSession) {
            mappingSection.hidden = true;
            return;
        }
        mappingSection.hidden = false;
        mappingBody.innerHTML = '';
        const count = Math.min(refSession.pageCount, tgtSession.pageCount);
        for (let i = 1; i <= count; i++) {
            addMappingRow(i, i);
        }
    }

    function addMappingRow(refPage, tgtPage) {
        const tr = document.createElement('tr');

        const tdRef = document.createElement('td');
        tdRef.appendChild(createPageSelect(refSession.pageCount, refPage, 'ref'));
        tr.appendChild(tdRef);

        const tdTgt = document.createElement('td');
        tdTgt.appendChild(createPageSelect(tgtSession.pageCount, tgtPage, 'tgt'));
        tr.appendChild(tdTgt);

        const tdAction = document.createElement('td');
        const removeBtn = document.createElement('button');
        removeBtn.className = 'remove-btn';
        removeBtn.textContent = '✕';
        removeBtn.title = '삭제';
        removeBtn.addEventListener('click', () => {
            tr.remove();
            updateCompareBtn();
        });
        tdAction.appendChild(removeBtn);
        tr.appendChild(tdAction);

        mappingBody.appendChild(tr);
        updateCompareBtn();
    }

    function createPageSelect(pageCount, selected, prefix) {
        const sel = document.createElement('select');
        for (let i = 1; i <= pageCount; i++) {
            const opt = document.createElement('option');
            opt.value = i;
            opt.textContent = `${i} 페이지`;
            if (i === selected) opt.selected = true;
            sel.appendChild(opt);
        }
        sel.dataset.type = prefix;
        return sel;
    }

    addMappingBtn.addEventListener('click', () => {
        if (!refSession || !tgtSession) return;
        const lastRow = mappingBody.lastElementChild;
        let nextRef = 1, nextTgt = 1;
        if (lastRow) {
            nextRef = Math.min(+lastRow.querySelector('[data-type=ref]').value + 1, refSession.pageCount);
            nextTgt = Math.min(+lastRow.querySelector('[data-type=tgt]').value + 1, tgtSession.pageCount);
        }
        addMappingRow(nextRef, nextTgt);
    });

    function getMappings() {
        const rows = mappingBody.querySelectorAll('tr');
        return Array.from(rows).map(row => ({
            refPage: +row.querySelector('[data-type=ref]').value,
            tgtPage: +row.querySelector('[data-type=tgt]').value
        }));
    }

    // === Compare Button ===

    function updateCompareBtn() {
        const hasFiles = refSession && tgtSession;
        const hasPrompt = currentPromptContent || promptEditor.value;
        const hasMappings = mappingBody.querySelectorAll('tr').length > 0;
        compareBtn.disabled = isComparing || !(hasFiles && hasPrompt);

        // Show/hide hint and table based on mapping rows
        if (hasFiles) {
            mappingHint.hidden = hasMappings;
            mappingTable.hidden = !hasMappings;
        }
    }

    // === Prompt ===

    async function loadPrompts() {
        const res = await fetch('/api/prompts');
        const list = await res.json();
        promptSelect.innerHTML = '';
        list.forEach((name) => {
            const opt = document.createElement('option');
            opt.value = name;
            opt.textContent = name;
            promptSelect.appendChild(opt);
        });
        if (list.length > 0) await selectPrompt(list[0]);
    }

    async function selectPrompt(name) {
        const res = await fetch(`/api/prompts/${name}`);
        currentPromptContent = await res.text();
        promptEditor.value = currentPromptContent;
        updateCompareBtn();
    }

    promptSelect.addEventListener('change', () => selectPrompt(promptSelect.value));

    editPromptBtn.addEventListener('click', () => {
        const isHidden = promptEditor.hidden;
        promptEditor.hidden = !isHidden;
        editPromptBtn.textContent = isHidden ? '접기' : '편집';
    });

    promptEditor.addEventListener('input', () => {
        currentPromptContent = promptEditor.value;
        updateCompareBtn();
    });

    // === Full-Document Compare (no mappings) ===

    async function runFullCompare() {
        progressContainer.hidden = false;
        progressFill.style.width = '0%';
        progressText.textContent = '전체 페이지 비교 중...';
        progressTokens.textContent = '';

        try {
            const fd = new FormData();
            fd.append('reference', refFile);
            fd.append('target', tgtFile);

            if (promptEditor.value !== currentPromptContent || !promptEditor.hidden) {
                fd.append('customPrompt', promptEditor.value);
            } else {
                fd.append('prompt', promptSelect.value);
            }

            const res = await fetch('/api/compare', { method: 'POST', body: fd });
            if (!res.ok) {
                const err = await res.json().catch(() => ({ error: res.statusText }));
                throw new Error(err.error || 'Comparison failed');
            }
            const markdown = await res.text();
            reportContent.innerHTML = marked.parse(markdown);
            progressFill.style.width = '100%';
            progressText.textContent = '전체 비교 완료';
        } catch (e) {
            reportContent.innerHTML = `<p style="color:red;">오류: ${e.message}</p>`;
        }
        progressContainer.hidden = true;
    }

    // === Page-by-Page Compare ===

    compareBtn.addEventListener('click', async () => {
        const mappings = getMappings();

        isComparing = true;
        updateCompareBtn();
        reportSection.hidden = false;
        reportContent.innerHTML = '';
        tokenSummary.hidden = true;
        tokenBody.innerHTML = '';
        tokenFoot.innerHTML = '';

        // No mappings → full-document comparison
        if (mappings.length === 0) {
            await runFullCompare();
            isComparing = false;
            updateCompareBtn();
            return;
        }

        progressContainer.hidden = false;
        progressFill.style.width = '0%';
        progressText.textContent = `0 / ${mappings.length} 페이지 완료`;
        progressTokens.textContent = '';

        const tokenResults = [];
        let totalPrompt = 0, totalCompletion = 0;

        for (let i = 0; i < mappings.length; i++) {
            const m = mappings[i];
            progressText.textContent = `${i} / ${mappings.length} 페이지 완료 — 페이지 ${m.refPage}↔${m.tgtPage} 비교 중...`;

            try {
                const fd = new FormData();
                fd.append('refId', refSession.id);
                fd.append('tgtId', tgtSession.id);
                fd.append('refPage', m.refPage);
                fd.append('tgtPage', m.tgtPage);

                if (promptEditor.value !== currentPromptContent || !promptEditor.hidden) {
                    fd.append('customPrompt', promptEditor.value);
                } else {
                    fd.append('prompt', promptSelect.value);
                }

                const res = await fetch('/api/compare-page', { method: 'POST', body: fd });
                if (!res.ok) {
                    const err = await res.json().catch(() => ({ error: res.statusText }));
                    throw new Error(err.error || 'Comparison failed');
                }

                const data = await res.json();

                // Append report
                const pageHeader = document.createElement('div');
                pageHeader.className = 'page-report';
                pageHeader.innerHTML =
                    `<h3>페이지 ${m.refPage} (기준) ↔ ${m.tgtPage} (작업)</h3>` +
                    marked.parse(data.text);
                reportContent.appendChild(pageHeader);

                // Track tokens
                tokenResults.push({
                    label: `${m.refPage}↔${m.tgtPage}`,
                    prompt: data.promptTokens,
                    completion: data.completionTokens,
                    total: data.totalTokens
                });
                totalPrompt += data.promptTokens;
                totalCompletion += data.completionTokens;

            } catch (e) {
                const errDiv = document.createElement('div');
                errDiv.className = 'page-report';
                errDiv.innerHTML =
                    `<h3>페이지 ${m.refPage} ↔ ${m.tgtPage}</h3>` +
                    `<p style="color:red;">오류: ${e.message}</p>`;
                reportContent.appendChild(errDiv);

                tokenResults.push({
                    label: `${m.refPage}↔${m.tgtPage}`,
                    prompt: 0, completion: 0, total: 0
                });
            }

            const done = i + 1;
            progressFill.style.width = `${(done / mappings.length) * 100}%`;
            progressText.textContent = `${done} / ${mappings.length} 페이지 완료`;
            progressTokens.textContent = `토큰: ${(totalPrompt + totalCompletion).toLocaleString()}`;
        }

        // Token summary table
        tokenBody.innerHTML = tokenResults.map(t =>
            `<tr><td>${t.label}</td><td>${t.prompt.toLocaleString()}</td><td>${t.completion.toLocaleString()}</td><td>${t.total.toLocaleString()}</td></tr>`
        ).join('');
        tokenFoot.innerHTML =
            `<tr><td>합계</td><td>${totalPrompt.toLocaleString()}</td><td>${totalCompletion.toLocaleString()}</td><td>${(totalPrompt + totalCompletion).toLocaleString()}</td></tr>`;
        tokenSummary.hidden = false;

        isComparing = false;
        updateCompareBtn();
    });

    // === Settings ===

    let originalMaskedKey = '';

    settingsBtn.addEventListener('click', async () => {
        const res = await fetch('/api/settings');
        const settings = await res.json();
        originalMaskedKey = settings.apiKey || '';
        apiKeyInput.value = '';
        apiKeyInput.placeholder = originalMaskedKey ? `현재: ${originalMaskedKey}` : 'API Key 입력';
        modelInput.value = settings.model || 'gpt-4o';
        settingsModal.hidden = false;
        loadModels().then(() => {
            const current = settings.model || 'gpt-4o';
            if ([...modelSelect.options].some(o => o.value === current)) {
                modelSelect.value = current;
            }
        });
    });

    closeSettingsBtn.addEventListener('click', () => { settingsModal.hidden = true; });
    settingsModal.addEventListener('click', (e) => { if (e.target === settingsModal) settingsModal.hidden = true; });

    saveSettingsBtn.addEventListener('click', async () => {
        const payload = { model: modelInput.value };
        if (apiKeyInput.value) payload.apiKey = apiKeyInput.value;
        await fetch('/api/settings', {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(payload)
        });
        settingsModal.hidden = true;
    });

    async function loadModels() {
        modelSelect.innerHTML = '<option value="">로딩 중...</option>';
        try {
            const res = await fetch('/api/models');
            if (!res.ok) {
                modelSelect.innerHTML = '<option value="">API key 필요</option>';
                return;
            }
            const data = await res.json();
            const models = (data.data || [])
                .map(m => m.id)
                .filter(id => id.startsWith('gpt-') || id.startsWith('o'))
                .sort();
            modelSelect.innerHTML = '<option value="">(직접 입력)</option>';
            models.forEach(id => {
                const opt = document.createElement('option');
                opt.value = id;
                opt.textContent = id;
                modelSelect.appendChild(opt);
            });
        } catch {
            modelSelect.innerHTML = '<option value="">(목록 로드 실패)</option>';
        }
    }

    modelSelect.addEventListener('change', () => {
        if (modelSelect.value) modelInput.value = modelSelect.value;
    });

    // Init
    loadPrompts();
})();
