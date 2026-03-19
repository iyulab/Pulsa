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
    const modelInput = document.getElementById('modelInput');
    const saveSettingsBtn = document.getElementById('saveSettingsBtn');
    const closeSettingsBtn = document.getElementById('closeSettingsBtn');
    const loadingOverlay = document.getElementById('loadingOverlay');

    function setupDropZone(zone, input, nameEl, setFile) {
        zone.addEventListener('click', () => input.click());
        zone.addEventListener('dragover', (e) => { e.preventDefault(); zone.classList.add('dragover'); });
        zone.addEventListener('dragleave', () => zone.classList.remove('dragover'));
        zone.addEventListener('drop', (e) => {
            e.preventDefault();
            zone.classList.remove('dragover');
            const file = e.dataTransfer.files[0];
            if (file && file.type === 'application/pdf') { setFile(file); nameEl.textContent = file.name; zone.classList.add('has-file'); }
        });
        input.addEventListener('change', () => {
            const file = input.files[0];
            if (file) { setFile(file); nameEl.textContent = file.name; zone.classList.add('has-file'); }
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

    settingsBtn.addEventListener('click', async () => {
        const res = await fetch('/api/settings');
        const settings = await res.json();
        apiKeyInput.value = settings.apiKey || '';
        modelInput.value = settings.model || 'gpt-4o';
        settingsModal.hidden = false;
    });

    closeSettingsBtn.addEventListener('click', () => { settingsModal.hidden = true; });
    settingsModal.addEventListener('click', (e) => { if (e.target === settingsModal) settingsModal.hidden = true; });

    saveSettingsBtn.addEventListener('click', async () => {
        await fetch('/api/settings', {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ apiKey: apiKeyInput.value, model: modelInput.value })
        });
        settingsModal.hidden = true;
    });

    loadPrompts();
})();
