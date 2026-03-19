(function () {
    let refFile = null;
    let tgtFile = null;
    let currentPromptContent = '';

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
    const loadingOverlay = document.getElementById('loadingOverlay');

    function setupDropZone(zone, input, nameEl, setFile) {
        const hintEl = zone.querySelector('.drop-hint');
        function applyFile(file) {
            setFile(file);
            nameEl.textContent = file.name;
            zone.classList.add('has-file');
            if (hintEl) hintEl.textContent = '클릭하여 파일 변경';
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

    setupDropZone(refZone, refInput, refName, (f) => { refFile = f; updateCompareBtn(); });
    setupDropZone(tgtZone, tgtInput, tgtName, (f) => { tgtFile = f; updateCompareBtn(); });

    function updateCompareBtn() {
        compareBtn.disabled = !(refFile && tgtFile && (currentPromptContent || promptEditor.value));
    }

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

    compareBtn.addEventListener('click', async () => {
        const formData = new FormData();
        formData.append('reference', refFile);
        formData.append('target', tgtFile);

        if (promptEditor.value !== currentPromptContent || !promptEditor.hidden) {
            formData.append('customPrompt', promptEditor.value);
        } else {
            formData.append('prompt', promptSelect.value);
        }

        loadingOverlay.hidden = false;
        reportSection.hidden = true;

        try {
            const res = await fetch('/api/compare', { method: 'POST', body: formData });
            if (!res.ok) {
                const err = await res.json().catch(() => ({ error: res.statusText }));
                throw new Error(err.error || 'Comparison failed');
            }
            const markdown = await res.text();
            reportContent.innerHTML = marked.parse(markdown);
            reportSection.hidden = false;
        } catch (e) {
            reportContent.innerHTML = `<p style="color:red;">오류: ${e.message}</p>`;
            reportSection.hidden = false;
        } finally {
            loadingOverlay.hidden = true;
        }
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
            // Pre-select current model if it's in the list
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
        // Only include apiKey if user actually typed a new one
        if (apiKeyInput.value) payload.apiKey = apiKeyInput.value;
        await fetch('/api/settings', {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(payload)
        });
        settingsModal.hidden = true;
    });

    loadPrompts();
})();
