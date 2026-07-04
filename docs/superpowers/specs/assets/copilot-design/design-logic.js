class Component extends DCLogic {
  constructor(props){
    super(props);
    this.threadRef = React.createRef();
    this.carViewRef = React.createRef();
    this._aiEls = {};
    this._aiOverflow = {};
    this._aiHeight = {};
    this._everToggled = {};
    this._clampPx = 80;
    this._copiedId = null;
    // Auto-attached context for any feedback — user never types these.
    this.MODEL_VERSION = 'Copilot 2.4.1';
    this.REVIT_VERSION = 'Revit 2024.2';
    this.state = {
      theme: (props.defaultTheme === 'dark' ? 'dark' : 'light'),
      tab: 'chat',
      messages: [],
      input: '',
      typing: false,
      thinkPhase: 'idle',
      thinkLabel: '',
      thinkVisible: false,
      thinkExiting: false,
      showMention: false,
      showUsage: false,
      showUpgrade: false,
      planIdx: 1,
      panelW: 0,
      carDragging: false,
      carDragDX: 0,
      notified: false,
      portalOpening: false,
      warnDismissed: false,
      expanded: {},
      feedback: {},
      macroOpen: false,
      macroForm: null,
      rating: null,
      ratingDismissed: {},
      _id: 1,
    };
  }

  componentDidMount(){
    this._measure();
    // Pin the thread to the bottom on ANY height change while generating — the
    // long-message "Show more" clamp re-measures asynchronously and would
    // otherwise leave the thinking line below the fold.
    try {
      this._ro = new ResizeObserver(() => {
        if (this._stick){ const e = this.threadRef.current; if (e) e.scrollTop = e.scrollHeight; }
      });
      const el = this.threadRef.current;
      if (el){ this._ro.observe(el); for (const c of el.children) this._ro.observe(c); }
    } catch(e){}
  }

  // Detect which AI messages overflow the clamp so the toggle only shows when needed.
  _measure(){
    let changed = false;
    for (const id in this._aiEls){
      const el = this._aiEls[id];
      if (!el) continue;
      this._aiHeight[id] = el.scrollHeight;
      const over = el.scrollHeight > (this._clampPx + 8);
      if (!!this._aiOverflow[id] !== over){ this._aiOverflow[id] = over; changed = true; }
    }
    if (changed) this.forceUpdate();
  }

  componentDidUpdate(){
    // Measure the carousel viewport width so the peek transforms are exact.
    if (this.state.showUpgrade && this.carViewRef && this.carViewRef.current){
      const w = this.carViewRef.current.clientWidth;
      if (w && w !== this.state.panelW) this.setState({ panelW: w });
    }
    const el = this.threadRef.current;
    if (!el) return;
    // Re-observe children so the ResizeObserver tracks newly-added messages.
    if (this._ro){ try { this._ro.disconnect(); this._ro.observe(el); for (const c of el.children) this._ro.observe(c); } catch(e){} }
    const n = this.state.messages.length;
    // "Stick to bottom" while a generation is active (or a new message just
    // arrived) so async height changes keep the thinking line in view.
    this._stick = this.state.typing || this.state.thinkVisible || n !== this._lastN;
    if (n !== this._lastN || this.state.typing !== this._lastTyping || this.state.thinkLabel !== this._lastLabel || this.state.thinkPhase !== this._lastPhase){
      el.scrollTop = el.scrollHeight;
      cancelAnimationFrame(this._scrollRaf);
      this._scrollRaf = requestAnimationFrame(() => {
        const e = this.threadRef.current; if (e) e.scrollTop = e.scrollHeight;
        this._scrollRaf2 = requestAnimationFrame(() => { const e2 = this.threadRef.current; if (e2) e2.scrollTop = e2.scrollHeight; });
      });
    }
    this._lastN = n;
    this._lastTyping = this.state.typing;
    this._lastLabel = this.state.thinkLabel;
    this._lastPhase = this.state.thinkPhase;
    this._measure();
  }

  componentWillUnmount(){ this._clearThink(); cancelAnimationFrame(this._scrollRaf); cancelAnimationFrame(this._scrollRaf2); if (this._ro) try { this._ro.disconnect(); } catch(e){} }
  _clearThink(){ (this._thinkTimers || []).forEach(clearTimeout); this._thinkTimers = []; }

  now(){
    const d = new Date();
    let h = d.getHours();
    const m = String(d.getMinutes()).padStart(2,'0');
    const ap = h >= 12 ? 'PM' : 'AM';
    h = h % 12 || 12;
    return h + ':' + m + ' ' + ap;
  }

  nextId(){ const id = this.state._id; this.setState(s => ({ _id: s._id + 1 })); return id; }

  answerFor(text){
    const t = text.toLowerCase();
    if (/wall/.test(t)) return "Done — I created the walls on Level 2 running along grid A → F using the Generic 200 mm wall type. You'll see them in the active view now.";
    if (/schedule/.test(t)) return "I've generated the door schedule, sorted by level with the Mark, Level and Width fields. It's been added to your project's schedules.";
    if (/tag/.test(t)) return "All rooms on Level 1 are now tagged with name and number, placed horizontally. Let me know if you'd like a different tag family.";
    if (/window/.test(t)) return "Windows are placed along the south facade at 2,400 mm spacing using the Fixed 1200×1500 family. Adjust the spacing anytime and I'll update them.";
    if (/door/.test(t)) return "I placed a Single-Flush 900×2100 door on the selected wall at Level 1. Tell me if you'd like it mirrored or moved.";
    if (/section|view/.test(t)) return "I created a section view at grid 4 (6,000 mm depth, 1:50 scale). It's open in your views list.";
    return "I can help with that. Tell me a level, category, or selection to act on — or type @ to reference one directly.";
  }

  // ── Thinking / progress ────────────────────────────────────────────────
  // ── Thinking / progress ────────────────────────────────────────────────
  // ONE status line, driven by progress EVENTS (not a fixed timer). Only the
  // CURRENT step is shown; each event replaces the line. In production, replace
  // _simulateBackend() with a real subscription that calls this._emit(ev) for
  // each event streamed from the server:
  //   { type:'thinking' }                  → line shows "Thinking" + spinner
  //   { type:'step_started', label }       → line swaps to this step's label
  //   { type:'step_completed' }            → no visual change (next step swaps it)
  //   { type:'done' }                      → spinner → check, line shows "Done"
  // Step count and labels vary per request; the line always shows just the
  // current one — the panel never grows taller.

  // Map known backend step keys to friendly wording; unknown keys are
  // humanised (snake/camel → Title case) and shown as-is.
  friendlyStep(label){
    if (!label) return '';
    const map = {
      thinking: 'Thinking',
      understand: 'Understanding your request',
      parse_request: 'Understanding your request',
      retrieve_context: 'Looking through the model',
      search_model: 'Looking through the model',
      read_model: 'Looking through the model',
      plan: 'Planning the approach',
      reason: 'Reasoning it through',
      generate: 'Putting together a response',
      compose: 'Putting together a response',
      build_command: 'Preparing the command',
      validate: 'Double-checking the result',
      verify: 'Double-checking the result',
    };
    const key = String(label).trim().toLowerCase().replace(/[\s-]+/g, '_');
    if (map[key]) return map[key];
    // humanise an unmapped custom label
    return String(label)
      .replace(/[_-]+/g, ' ')
      .replace(/([a-z])([A-Z])/g, '$1 $2')
      .replace(/^./, c => c.toUpperCase());
  }

  // Apply ONE backend event — only the current line changes.
  _emit(ev){
    this.setState(() => {
      if (ev.type === 'thinking') return { thinkPhase:'working', thinkLabel:'Thinking' };
      if (ev.type === 'step_started') return { thinkPhase:'working', thinkLabel: this.friendlyStep(ev.label) };
      if (ev.type === 'done') return { thinkPhase:'done', thinkLabel:'Done' };
      return {}; // step_completed: no visual change for a single-line indicator
    });
  }

  // DEMO ONLY — fakes a backend stream so the motion is visible in the mockup.
  // Production must delete this and bind _emit() to real server events.
  _simulateBackend(onDone){
    const scenarios = [
      ['parse_request', 'generate'],
      ['parse_request', 'retrieve_context', 'generate', 'validate'],
      ['parse_request', 'retrieve_context', 'plan', 'generate', 'optimize_layout', 'validate'],
      ['parse_request', 'search_model', 'build_command'],
    ];
    const pick = scenarios[Math.floor(Math.random() * scenarios.length)];
    // Each generation carries an id; any timer whose id is stale (because the
    // user sent again, stopped, or started a new chat) becomes a no-op. This is
    // what keeps the thinking line reliable across rapid / interrupted sends.
    const gen = this._genId;
    const tm = []; let at = 0;
    const fire = (fn, d) => { at += d; tm.push(setTimeout(() => { if (this._genId === gen) fn(); }, at)); };
    fire(() => this._emit({ type:'thinking' }), 500);
    pick.forEach((label) => {
      fire(() => this._emit({ type:'step_started', label }), 900 + Math.random() * 200);
      fire(() => this._emit({ type:'step_completed' }), 10);
    });
    fire(() => this._emit({ type:'done' }), 700);
    fire(() => onDone(), 650);
    this._thinkTimers = tm;
  }

  send(text){
    const raw = (text != null ? text : this.state.input);
    const t = (raw || '').trim();
    if (!t || this.state.typing) return;
    const uid = Date.now();
    const userMsg = { id: uid, role:'user', text: t, time: this.now() };
    this._genId = (this._genId || 0) + 1;
    const gen = this._genId;
    this.setState(s => ({ messages: [...s.messages, userMsg], input:'', showMention:false, typing:true, thinkVisible:true, thinkExiting:false, thinkPhase: 'working', thinkLabel: 'Thinking' }));
    this._clearThink();
    this._simulateBackend(() => {
      const ai = {
        id: uid + 1,
        role:'ai',
        text: this.answerFor(t),
        time: this.now(),
      };
      // Cross-fade: drop typing (send button reverts) and add the answer, but keep
      // the thinking line mounted in an exit animation so it fades out as the
      // answer fades in — then unmount it once the fade completes.
      this.setState(s => ({ messages: [...s.messages, ai], typing:false, thinkExiting:true }));
      this._thinkTimers.push(setTimeout(() => {
        if (this._genId === gen) this.setState({ thinkVisible:false, thinkExiting:false, thinkPhase:'idle', thinkLabel:'' });
      }, 260));
    });
  }

  setStatus(id, status){
    this.setState(s => ({ messages: s.messages.map(m => (m.id === id && m.command) ? { ...m, command: { ...m.command, status } } : m) }));
  }

  toggleTheme(){ this.setState(s => ({ theme: s.theme === 'dark' ? 'light' : 'dark' })); }
  toggleUsage(){ this.setState(s => ({ showUsage: !s.showUsage })); }
  closeUsage(){ this.setState({ showUsage: false }); }
  openUpgrade(){ this.setState({ showUpgrade: true, showUsage: false, planIdx: 1, carDragDX: 0, carDragging: false }); }
  closeUpgrade(){ this.setState({ showUpgrade: false, portalOpening: false }); }

  // ===== Peek carousel =====
  carGo(i){ const n = Math.max(0, Math.min(2, i)); this.setState({ planIdx: n, carDragDX: 0 }); }
  carPrev(){ this.carGo((this.state.planIdx ?? 1) - 1); }
  carNext(){ this.carGo((this.state.planIdx ?? 1) + 1); }
  carDown(e){ this._carX0 = e.clientX; this._carMoved = false; this.setState({ carDragging: true, carDragDX: 0 }); try { e.currentTarget.setPointerCapture(e.pointerId); } catch(_){} }
  carMove(e){ if (!this.state.carDragging) return; const dx = e.clientX - this._carX0; if (Math.abs(dx) > 3) this._carMoved = true; this.setState({ carDragDX: dx }); }
  carUp(){ if (!this.state.carDragging) return; const dx = this.state.carDragDX || 0; const W = this.state.panelW || 320; const thresh = W * 0.16; let idx = this.state.planIdx ?? 1; if (dx <= -thresh) idx += 1; else if (dx >= thresh) idx -= 1; this.carGo(idx); this.setState({ carDragging: false }); }
  notifyAdmin(){ this.setState({ notified: true }); }
  dismissWarn(){ this.setState({ warnDismissed: true }); }

  // ---- MICRO: per-response feedback ----
  _fb(id){ return this.state.feedback[id] || {}; }
  _setFb(id, patch){
    this.setState(s => ({ feedback: { ...s.feedback, [id]: { ...(s.feedback[id] || {}), ...patch } } }));
  }
  voteUp(id){
    const cur = this._fb(id);
    // Silent: toggle highlight only, close any open downvote panel.
    this._setFb(id, { vote: cur.vote === 'up' ? null : 'up', panelOpen: false });
  }
  voteDown(id){
    const cur = this._fb(id);
    if (cur.vote === 'down'){ this._setFb(id, { vote: null, panelOpen: false }); return; }
    this._setFb(id, { vote: 'down', panelOpen: true, submitted: false });
  }
  closeFeedbackPanel(id){ this._setFb(id, { panelOpen: false }); }
  pickReason(id, reason){
    const cur = this._fb(id);
    this._setFb(id, { reason: cur.reason === reason ? null : reason });
  }
  setFeedbackNote(id, text){ this._setFb(id, { note: text }); }
  submitMicroFeedback(id){ this._setFb(id, { panelOpen: false, submitted: true }); }
  copyMessage(id, text){
    try { if (navigator.clipboard) navigator.clipboard.writeText(text || ''); } catch(e){}
    this._copiedId = id; this.forceUpdate();
    clearTimeout(this._copyTimer);
    this._copyTimer = setTimeout(() => { this._copiedId = null; this.forceUpdate(); }, 1600);
  }

  // ---- MACRO: general feedback (header kebab) ----
  toggleMacroMenu(){ this.setState(s => ({ macroOpen: !s.macroOpen, showUsage: false })); }
  closeMacroMenu(){ this.setState({ macroOpen: false }); }
  openMacroForm(mode){
    this.setState({ macroOpen: false, macroForm: { mode, type: mode === 'bug' ? 'bug' : 'suggestion', text: '', submitted: false } });
  }
  closeMacroForm(){ this.setState({ macroForm: null }); }

  // ---- Star rating (separate from bug report) ----
  openRating(){ this.setState({ macroOpen:false, macroForm:null, rating: { value:0, hover:0, pop:0, note:'', submitted:false } }); }
  closeRating(){ this.setState({ rating: null }); }
  setRatingHover(n){ this.setState(s => s.rating ? ({ rating: { ...s.rating, hover:n } }) : null); }
  pickRating(n){
    this.setState(s => s.rating ? ({ rating: { ...s.rating, value:n, pop:n } }) : null);
    clearTimeout(this._popTimer);
    this._popTimer = setTimeout(() => this.setState(s => s.rating ? ({ rating: { ...s.rating, pop:0 } }) : null), 360);
  }
  setRatingNote(text){ this.setState(s => s.rating ? ({ rating: { ...s.rating, note:text } }) : null); }
  submitRating(){ this.setState(s => (s.rating && s.rating.value) ? ({ rating: { ...s.rating, submitted:true } }) : null); }
  dismissNudge(id){ this.setState(s => ({ ratingDismissed: { ...s.ratingDismissed, [id]: true } })); }
  setMacroType(type){ this.setState(s => ({ macroForm: { ...s.macroForm, type } })); }
  setMacroText(text){ this.setState(s => ({ macroForm: { ...s.macroForm, text } })); }
  submitMacroForm(){ this.setState(s => ({ macroForm: { ...s.macroForm, submitted: true } })); }
  upgradeNow(){ try { window.open('https://billing.bina.cloud/upgrade', '_blank'); } catch(e){} this.setState({ portalOpening: true }); }
  seePlans(){ try { window.open('https://bina.cloud/pricing', '_blank'); } catch(e){} }  newChat(){ this._genId = (this._genId || 0) + 1; this._clearThink(); this.setState({ messages: [], tab:'chat', input:'', showMention:false, showUpgrade:false, notified:false, portalOpening:false, typing:false, thinkPhase:'idle', thinkLabel:'', thinkVisible:false, thinkExiting:false, expanded:{}, feedback:{}, macroOpen:false, macroForm:null, rating:null, ratingDismissed:{} }); }
  setTab(tab){ this.setState({ tab }); }

  stopGen(){
    this._genId = (this._genId || 0) + 1;
    this._clearThink();
    const note = { id: Date.now(), role:'ai', text: "Interrupted.", interrupted: true, time: this.now() };
    this.setState(s => ({ messages: [...s.messages, note], typing:false, thinkPhase:'idle', thinkLabel:'', thinkVisible:false, thinkExiting:false }));
  }

  toggleExpand(id){ this._everToggled[id] = true; this.setState(s => ({ expanded: { ...s.expanded, [id]: !s.expanded[id] } })); }

  onInput(e){
    const v = e.target.value;
    this.setState({ input: v, showMention: /(^|\s)@\w*$/.test(v) });
  }
  onKey(e){
    if (e.key === 'Enter' && !e.shiftKey){ e.preventDefault(); this.send(); }
    if (e.key === 'Escape'){ this.setState({ showMention:false }); }
  }
  focusInput(){ setTimeout(() => { const el = document.getElementById('bina-input'); if (el) el.focus(); }, 0); }
  atBtn(){
    const v = this.state.input || '';
    const nv = v && !/\s$/.test(v) ? v + ' @' : v + '@';
    this.setState({ input: nv, showMention: true });
    this.focusInput();
  }
  pickMention(label){
    const v = (this.state.input || '').replace(/@\w*$/, '@' + label + ' ');
    this.setState({ input: v, showMention: false });
    this.focusInput();
  }

  loadHistory(conv){
    const base = Date.now();
    const messages = conv.thread.map((m, i) => ({
      id: base + i,
      role: m.role,
      text: m.text,
      time: m.time || conv.time,
      command: m.command ? { ...m.command, params: m.command.params.slice() } : undefined,
    }));
    this.setState({ messages, tab:'chat', input:'', showMention:false });
  }

  themeVars(isDark){
    const accent = this.props.accent;
    const gray = this.props.userBubble === 'gray';
    const base = "font-family:-apple-system,'Segoe UI',system-ui,Roboto,sans-serif;-webkit-font-smoothing:antialiased;display:flex;align-items:center;justify-content:center;min-height:100vh;padding:28px;";
    if (!isDark){
      const a = accent || '#2563eb';
      const user = gray ? "--user:#eef1f5;--user-text:#131c2b;" : "--user:linear-gradient(135deg,#84c5ff 0%,#4d88ef 52%,#7a78f3 100%);--user-text:#ffffff;";
      return "--bg:#ffffff;--sunken:#f3f6f9;--text:#131c2b;--text2:#586273;--text3:#99a3b3;--hair:rgba(15,27,45,.08);--hair2:rgba(15,27,45,.16);--accent:" + a + ";--accent-grad:linear-gradient(135deg, color-mix(in srgb, " + a + " 60%, #ffffff), " + a + ");--accent-contrast:#ffffff;" + user + "--logo:#131c2b;--menu:#ffffff;--green:#10b981;--hover:#f3f6f9;--lift:0 1px 3px rgba(15,27,45,.08), 0 1px 2px rgba(15,27,45,.04);--lift-hover:0 2px 6px rgba(15,27,45,.12), 0 1px 3px rgba(15,27,45,.06);--shadow:0 24px 60px rgba(15,27,45,.16);background:linear-gradient(160deg,#eef2f6,#e2e7ee);" + base;
    }
    const a = accent || '#60a5fa';
    const userD = gray ? "--user:#222e40;--user-text:#e8eef6;" : "--user:linear-gradient(135deg,#84c5ff 0%,#4d88ef 52%,#7a78f3 100%);--user-text:#ffffff;";
    return "--bg:#131d2b;--sunken:#0c1420;--text:#e8eef6;--text2:#8a94a6;--text3:#6b768a;--hair:rgba(255,255,255,.07);--hair2:rgba(255,255,255,.14);--accent:" + a + ";--accent-grad:linear-gradient(135deg, color-mix(in srgb, " + a + " 78%, #ffffff), " + a + ");--accent-contrast:#0c1420;" + userD + "--logo:#0c1420;--menu:#1a2433;--green:#34d399;--hover:rgba(255,255,255,.05);--lift:0 1px 3px rgba(0,0,0,.45), 0 1px 2px rgba(0,0,0,.3);--lift-hover:0 3px 8px rgba(0,0,0,.55), 0 1px 3px rgba(0,0,0,.35);--shadow:0 24px 60px rgba(5,9,16,.6);background:linear-gradient(160deg,#0b1119,#060a11);" + base;
  }

  renderVals(){
    const isDark = this.state.theme === 'dark';
    const tab = this.state.tab;
    const isChat = tab === 'chat';
    const hasMessages = this.state.messages.length > 0;
    const canSend = (this.state.input || '').trim().length > 0;

    const usagePct = Math.max(0, Math.min(100, Math.round(this.props.usage != null ? this.props.usage : 22)));
    const meterColor = usagePct >= 95 ? '#ef4444' : usagePct >= 80 ? '#f59e0b' : 'var(--accent)';
    const C = 2 * Math.PI * 7.4;
    const meterDash = (C * usagePct / 100).toFixed(2) + ' ' + C.toFixed(2);
    const placement = this.props.meterPlacement === 'footer' ? 'footer' : 'header';
    const atLimit = (this.props.atLimit === true) || usagePct >= 100;
    const isAdmin = true;
    const isFree = (this.props.plan || 'pro') === 'free';
    // Always show all three plans (Free · Basic · Pro) in the sheet.
    const planCfg = {
      meterName: 'Free', badge: 'Free',
      curName: 'Free', curPrice: '$0',
      curFeatures: [ { label:'Limited usage' }, { label:'Core Revit commands' }, { label:'Chat history' } ],
      recoName: 'Basic', recoPrice: '$20',
      recoFeatures: [ { label:'10× higher usage limit' }, { label:'Faster responses' }, { label:'Full Revit command library' }, { label:'Chat history & exports' }, { label:'Email support' } ],
      proName: 'Pro', proPrice: '$40',
      proFeatures: [ { label:'Everything in Basic' }, { label:'5× higher usage limit' }, { label:'Priority responses' }, { label:'Batch commands & automation' }, { label:'Priority support' } ],
      cta: 'Upgrade to Basic',
    };
    const typing = this.state.typing;
    const thinkExiting = this.state.thinkExiting;
    const thinkAnim = thinkExiting ? 'thinkOut .26s ease forwards' : 'msgRise .3s ease forwards';
    const phase = this.state.thinkPhase;
    const thinkWorking = phase === 'working';
    const thinkDone = phase === 'done';
    const thinkLabel = this.state.thinkLabel || '';
    // Single-line status: a keyed span so each label change remounts and
    // replays the fade-up swap; shimmer overlay while working.
    let thinkLineEl = null;
    if (thinkWorking || thinkDone){
      const baseStyle = {
        position:'absolute', left:0, top:0, whiteSpace:'nowrap',
        fontSize:'12.5px', letterSpacing:'-.005em', lineHeight:'18px',
        fontWeight: thinkWorking ? 600 : 560,
        animation:'swapUp .34s cubic-bezier(.2,.7,.3,1)',
      };
      if (thinkWorking){
        Object.assign(baseStyle, {
          backgroundImage:'linear-gradient(90deg, var(--text2) 38%, var(--accent) 50%, var(--text2) 62%)',
          backgroundSize:'220% 100%',
          WebkitBackgroundClip:'text', backgroundClip:'text',
          WebkitTextFillColor:'transparent', color:'transparent',
          animation:'swapUp .34s cubic-bezier(.2,.7,.3,1), shimmerText 2s linear .34s infinite',
        });
      } else {
        baseStyle.color = 'var(--text2)';
      }
      thinkLineEl = React.createElement('span', { key: thinkLabel + ':' + phase, style: baseStyle }, thinkLabel);
    }
    const showBlocked = isChat && atLimit && !typing && !this.state.showUpgrade;
    const showComposerNow = isChat && (!atLimit || typing);
    const pctLeft = Math.max(0, 100 - usagePct);
    const showWarn80 = isChat && !atLimit && usagePct >= 80 && usagePct < 95 && !this.state.warnDismissed;
    const showWarn95 = isChat && !atLimit && usagePct >= 95 && usagePct < 100;

    const tabBase = "height:38px;display:flex;align-items:center;gap:6px;border:0;background:transparent;cursor:pointer;font-size:13px;font-family:inherit;";
    const tabActive = tabBase + "font-weight:620;color:var(--text);border-bottom:2px solid var(--accent);";
    const tabIdle = tabBase + "font-weight:500;color:var(--text3);border-bottom:2px solid transparent;";

    const lastAiId = (() => { for (let i = this.state.messages.length - 1; i >= 0; i--){ const m = this.state.messages[i]; if (m.role === 'ai' && !m.interrupted) return m.id; } return null; })();
    const messages = this.state.messages.map(m => ({
      id: m.id,
      text: m.text,
      time: m.time,
      isUser: m.role === 'user',
      isAI: m.role === 'ai',
      interrupted: !!m.interrupted,
      isAIAnswer: m.role === 'ai' && !m.interrupted,
      hasCommand: !!m.command,
      cmd: m.command ? {
        name: m.command.name,
        params: m.command.params,
        isProposed: m.command.status === 'proposed',
        isApplied: m.command.status === 'applied',
        isDismissed: m.command.status === 'dismissed',
      } : null,
      apply: () => this.setStatus(m.id, 'applied'),
      dismiss: () => this.setStatus(m.id, 'dismissed'),
      showRatingNudge: m.role === 'ai' && !m.interrupted && m.id === lastAiId
        && this.state.messages.length > 1
        && !this.state.ratingDismissed[m.id]
        && !(this.state.rating && this.state.rating.submitted),
      dismissNudge: () => this.dismissNudge(m.id),
      setTextRef: (el) => { if (el) this._aiEls[m.id] = el; },
      needsToggle: !!this._aiOverflow[m.id],
      collapsedState: !this.state.expanded[m.id],
      expandedState: !!this.state.expanded[m.id],
      toggleLabel: this.state.expanded[m.id] ? 'Show less' : 'Show more',
      textWrapStyle: (() => {
        const ov = !!this._aiOverflow[m.id];
        if (!ov) return 'overflow:hidden;';
        const exp = !!this.state.expanded[m.id];
        const full = (this._aiHeight[m.id] || 1200);
        return 'overflow:hidden;max-height:' + (exp ? full + 'px' : this._clampPx + 'px') + ';'
          + (exp ? '' : '-webkit-mask-image:linear-gradient(to bottom,#000 58%,transparent);mask-image:linear-gradient(to bottom,#000 58%,transparent);');
      })(),
      toggleExpand: () => this.toggleExpand(m.id),
      fb: (() => {
        if (m.role !== 'ai') return null;
        const f = this.state.feedback[m.id] || {};
        const cmdName = m.command ? m.command.name : null;
        const ctxBits = [];
        if (cmdName) ctxBits.push(cmdName);
        ctxBits.push(this.MODEL_VERSION, this.REVIT_VERSION);
        const copied = this._copiedId === m.id;
        const btnBase = 'width:27px;height:27px;border:0;border-radius:7px;display:flex;align-items:center;justify-content:center;cursor:pointer;flex:none;transition:background .15s ease,color .15s ease;';
        const idle = 'background:transparent;color:var(--text3);';
        const onAccent = 'background:transparent;color:var(--accent);';
        const onGreen = 'background:transparent;color:var(--green);';
        const chipBase = 'padding:5px 10px;border-radius:7px;font-family:inherit;font-size:11px;font-weight:550;cursor:pointer;border:1px solid var(--hair);transition:background .15s ease,color .15s ease,border-color .15s ease;';
        return {
          votedUp: f.vote === 'up',
          votedDown: f.vote === 'down',
          showPrompt: !f.vote && !f.submitted,
          panelOpen: !!f.panelOpen,
          submitted: !!f.submitted,
          note: f.note || '',
          copied, notCopied: !copied,
          upStyle: btnBase + (f.vote === 'up' ? onAccent : idle),
          downStyle: btnBase + (f.vote === 'down' ? onAccent : idle),
          copyStyle: btnBase + (copied ? onGreen : idle),
          contextLabel: 'Auto-attached · ' + ctxBits.join(' · '),
          reasons: ['Not accurate','Wrong elements','Too slow','Other'].map(r => ({
            label: r, active: f.reason === r, pick: () => this.pickReason(m.id, r),
            chipStyle: chipBase + (f.reason === r ? 'background:color-mix(in srgb, var(--accent) 15%, transparent);color:var(--accent);border-color:transparent;' : 'background:transparent;color:var(--text2);'),
          })),
          voteUp: () => this.voteUp(m.id),
          voteDown: () => this.voteDown(m.id),
          closePanel: () => this.closeFeedbackPanel(m.id),
          setNote: (e) => this.setFeedbackNote(m.id, e.target.value),
          submit: () => this.submitMicroFeedback(m.id),
          copy: () => this.copyMessage(m.id, m.text),
        };
      })(),
    }));

    const mentions = [
      { label:'Level 2', type:'Level' },
      { label:'Level 1', type:'Level' },
      { label:'Walls', type:'Category' },
      { label:'Doors', type:'Category' },
      { label:'Floor Plan: L2', type:'View' },
      { label:'Current Selection', type:'Selection' },
    ].map(m => ({ ...m, pick: () => this.pickMention(m.label) }));

    const history = [
      { title:'Exterior walls — Level 2', time:'2:25 PM', meta:'2:25 PM · 3 messages', thread:[
        { role:'user', text:'Create exterior walls on Level 2 along grid A–F' },
        { role:'ai', text:"Done — I created the walls on Level 2 running along grid A → F using the Generic 200 mm wall type. You'll see them in the active view now." },
      ]},
      { title:'Door schedule for Block A', time:'11:31 AM', meta:'11:31 AM · 3 messages', thread:[
        { role:'user', text:'Generate a door schedule for Block A' },
        { role:'ai', text:"I've generated the door schedule, sorted by level with the Mark, Level and Width fields. It's been added to your project's schedules." },
      ]},
      { title:'Section view at grid 4', time:'Yesterday', meta:'Yesterday · 2 messages', thread:[
        { role:'user', text:'Create a section view at grid 4' },
        { role:'ai', text:"I created a section view at grid 4 (6,000 mm depth, 1:50 scale). It's open in your views list." },
      ]},
      { title:'Tag all rooms on Level 1', time:'Yesterday', meta:'Yesterday · 3 messages', thread:[
        { role:'user', text:'Tag all rooms on Level 1 with name and number' },
        { role:'ai', text:"All rooms on Level 1 are now tagged with name and number, placed horizontally. Let me know if you'd like a different tag family." },
      ]},
      { title:'Window placement — south facade', time:'2 days ago', meta:'2 days ago · 4 messages', thread:[
        { role:'user', text:'Place windows on the south facade' },
        { role:'ai', text:"Windows are placed along the south facade at 2,400 mm spacing using the Fixed 1200×1500 family. Adjust the spacing anytime and I'll update them." },
      ]},
    ].map(c => ({ title:c.title, meta:c.meta, open: () => this.loadHistory(c) }));

    // ===== Peek carousel geometry =====
    const carActive = this.state.planIdx ?? 1;
    const carW = this.state.panelW || 320;
    const cardW = Math.round(carW * 0.82);
    const carGap = 12;
    const carStep = cardW + carGap;
    const carDrag = this.state.carDragging ? (this.state.carDragDX || 0) : 0;
    const trackX = Math.round(carW / 2 - cardW / 2 - carActive * carStep + carDrag);
    const carTrackStyle = 'display:flex;align-items:stretch;gap:' + carGap + 'px;'
      + 'transform:translateX(' + trackX + 'px);'
      + 'transition:' + (this.state.carDragging ? 'none' : 'transform .32s cubic-bezier(.2,.8,.2,1)') + ';'
      + 'will-change:transform;';
    const planData = [
      { name:'Free', price:'$0', badge:false, incLabel:"What's included", ctaLabel:'Get started', kind:'outline', arrow:false,
        features:[ {label:'Limited usage'}, {label:'Core Revit commands'}, {label:'Chat history'} ] },
      { name:'Basic', price:'$20', badge:true, incLabel:"What's included", ctaLabel:'Upgrade to Basic', kind:'solid', arrow:true,
        features:[ {label:'10× higher usage limit'}, {label:'Faster responses'}, {label:'Full Revit command library'}, {label:'Chat history & exports'}, {label:'Email support'} ] },
      { name:'Pro', price:'$40', badge:false, incLabel:'Everything in Basic, plus', ctaLabel:'Upgrade to Pro', kind:'solid', arrow:true,
        features:[ {label:'Everything in Basic'}, {label:'5× higher usage limit'}, {label:'Priority responses'}, {label:'Batch commands & automation'}, {label:'Priority support'} ] },
    ];
    const carPlans = planData.map((p, i) => {
      const isActive = i === carActive;
      const origin = isActive ? 'center' : (i < carActive ? 'right center' : 'left center');
      const cardStyle = 'box-sizing:border-box;flex:0 0 ' + cardW + 'px;'
        + 'position:relative;z-index:' + (isActive ? 3 : 1) + ';'
        + 'display:flex;flex-direction:column;'
        + 'border-radius:15px;padding:15px 15px 15px;background:var(--bg);'
        + 'border:' + (p.badge ? '1.5px solid var(--accent)' : '1px solid var(--hair)') + ';'
        + 'box-shadow:' + (isActive ? '0 8px 22px rgba(6,10,18,.10)' : 'none') + ';'
        + 'opacity:' + (isActive ? 1 : 0.45) + ';'
        + 'transform:scale(' + (isActive ? 1 : 0.9) + ');transform-origin:' + origin + ';'
        + 'transition:transform .32s cubic-bezier(.2,.8,.2,1), opacity .32s ease, box-shadow .32s ease;';
      const useAccent = p.badge;
      const checkStyle = 'display:flex;flex:none;color:' + (useAccent ? 'var(--accent)' : 'var(--text3)') + ';';
      const featStyle = 'font-size:12px;color:' + (useAccent ? 'var(--text)' : 'var(--text2)') + ';font-weight:' + (useAccent ? 550 : 400) + ';';
      const ctaStyle = 'width:100%;height:38px;margin-top:2px;border-radius:10px;font-family:inherit;font-size:12.5px;font-weight:650;letter-spacing:-.01em;cursor:pointer;display:flex;align-items:center;justify-content:center;gap:6px;transition:background .3s ease, color .3s ease, border-color .3s ease;'
        + (!isActive
            ? 'border:1px solid var(--hair);background:var(--sunken);color:var(--text3);'
            : (p.kind === 'solid'
                ? 'border:1px solid transparent;background:var(--accent-grad);color:var(--accent-contrast);'
                : 'border:1px solid var(--accent);background:transparent;color:var(--accent);'));
      return { name:p.name, price:p.price, badge:p.badge, incLabel:p.incLabel, features:p.features,
        ctaLabel:p.ctaLabel, ctaArrow:p.arrow, cardStyle, checkStyle, featStyle, ctaStyle,
        onCta: () => this.upgradeNow() };
    });
    const carDots = planData.map((p, i) => {
      const on = i === carActive;
      const style = 'height:6px;border-radius:99px;border:0;cursor:pointer;padding:0;'
        + 'width:' + (on ? '18px' : '6px') + ';'
        + 'background:' + (on ? 'var(--accent)' : 'var(--hair2)') + ';'
        + 'transition:width .28s cubic-bezier(.2,.8,.2,1), background .2s ease;';
      return { style, go: () => this.carGo(i) };
    });
    const carArrowBase = 'width:30px;height:30px;border-radius:9px;display:flex;align-items:center;justify-content:center;flex:none;font-family:inherit;background:transparent;border:0;';
    const carPrevStyle = carArrowBase + (carActive <= 0
      ? 'color:var(--text3);opacity:.35;cursor:default;pointer-events:none;'
      : 'color:var(--text2);cursor:pointer;');
    const carNextStyle = carArrowBase + (carActive >= 2
      ? 'color:var(--text3);opacity:.35;cursor:default;pointer-events:none;'
      : 'color:var(--text2);cursor:pointer;');

    return {
      rootStyle: this.themeVars(isDark),
      isDark, isLight: !isDark,
      historyCount: history.length,
      chatTabStyle: isChat ? tabActive : tabIdle,
      histTabStyle: !isChat ? tabActive : tabIdle,
      showEmpty: isChat && !hasMessages && !atLimit,
      showThread: isChat && hasMessages,
      showHistory: !isChat,
      showComposer: showComposerNow,
      showBlocked,
      blockedWrapStyle: hasMessages
        ? "flex:none;padding:18px 18px;border-top:1px solid var(--hair);display:flex;flex-direction:column;align-items:center;text-align:center;gap:9px;"
        : "flex:1;min-height:0;padding:22px 22px 90px;display:flex;flex-direction:column;align-items:center;justify-content:center;text-align:center;gap:9px;",
      showBlockedAdmin: isAdmin,
      showBlockedMember: !isAdmin,
      showWarn80, showWarn95, pctLeft,
      dismissWarn: () => this.dismissWarn(),
      showUpgrade: this.state.showUpgrade && isChat,
      currentName: planCfg.curName, currentPrice: planCfg.curPrice, currentFeatures: planCfg.curFeatures,
      recoName: planCfg.recoName, recoPrice: planCfg.recoPrice, recoFeatures: planCfg.recoFeatures,
      proName: planCfg.proName, proPrice: planCfg.proPrice, proFeatures: planCfg.proFeatures,
      ctaLabel: planCfg.cta,
      upgradeBenefits: [
        { label: '5× higher usage limit' },
        { label: 'Priority responses' },
        { label: 'Advanced model access' },
      ],
      showUpgradeBtn: isAdmin && !this.state.portalOpening,
      showPortalNote: isAdmin && this.state.portalOpening,
      showNotifyBtn: !isAdmin && !this.state.notified,
      showNotifiedNote: !isAdmin && this.state.notified,
      openUpgrade: () => this.openUpgrade(),
      closeUpgrade: () => this.closeUpgrade(),
      upgradeNow: () => this.upgradeNow(),
      carViewRef: this.carViewRef,
      carTrackStyle, carPlans, carDots, carPrevStyle, carNextStyle,
      carPrev: () => this.carPrev(),
      carNext: () => this.carNext(),
      carDown: (e) => this.carDown(e),
      carMove: (e) => this.carMove(e),
      carUp: () => this.carUp(),
      notifyAdmin: () => this.notifyAdmin(),
      seePlans: () => this.seePlans(),
      messages, typing: this.state.typing,
      thinking: this.state.thinkVisible, thinkWorking, thinkDone, thinkLineEl, thinkAnim,
      input: this.state.input,
      showMention: this.state.showMention,
      mentionItems: mentions,
      historyList: history,
      threadRef: this.threadRef,
      showSendLoading: this.state.typing,
      showSendActive: !this.state.typing && canSend,
      showSendIdle: !this.state.typing && !canSend,
      usagePct, usageWidth: usagePct + '%', meterColor, meterDash,
      planName: planCfg.meterName, planBadge: planCfg.badge,
      showUsage: this.state.showUsage,
      showHeaderMeter: placement === 'header',
      showFooterMeter: placement === 'footer' && isChat,
      showHeaderUsagePop: this.state.showUsage && placement === 'header',
      showFooterUsagePop: this.state.showUsage && placement === 'footer',
      toggleUsage: () => this.toggleUsage(),
      closeUsage: () => this.closeUsage(),
      goChat: () => this.setTab('chat'),
      goHistory: () => this.setTab('history'),
      sendBtn: () => this.send(),
      stopGen: () => this.stopGen(),
      onInput: (e) => this.onInput(e),
      onKey: (e) => this.onKey(e),
      atBtn: () => this.atBtn(),
      toggleTheme: () => this.toggleTheme(),
      newChat: () => this.newChat(),
      macroMenuOpen: this.state.macroOpen,
      toggleMacroMenu: () => this.toggleMacroMenu(),
      closeMacroMenu: () => this.closeMacroMenu(),
      openMacroFeedback: () => this.openMacroForm('feedback'),
      openMacroBug: () => this.openMacroForm('bug'),
      openRating: () => this.openRating(),
      closeRating: () => this.closeRating(),
      clearRatingHover: () => this.setRatingHover(0),
      setRatingNote: (e) => this.setRatingNote(e && e.target ? e.target.value : ''),
      submitRating: () => this.submitRating(),
      ratingOpen: !!this.state.rating,
      ratingActive: !!(this.state.rating && !this.state.rating.submitted),
      ratingSubmitted: !!(this.state.rating && this.state.rating.submitted),
      ratingNote: this.state.rating ? this.state.rating.note : '',
      ratingContextLabel: this.MODEL_VERSION + ' · ' + this.REVIT_VERSION,
      ratingValueLabel: (this.state.rating ? this.state.rating.value : 0) + '-star',
      ratingStars: (() => {
        const r = this.state.rating || { value:0, hover:0, pop:0 };
        const level = r.hover || r.value;
        const out = [];
        for (let i = 0; i < 5; i++) {
          const on = i < level;
          out.push({
            on,
            fill: on ? 'url(#gstarGold)' : 'none',
            stroke: on ? '#E8941A' : 'var(--hair2)',
            shine: on ? 'filter:drop-shadow(0 1px 2px rgba(232,148,26,.5));' : '',
            anim: (r.pop === i + 1) ? 'animation:starPop .36s ease;' : '',
            enter: () => this.setRatingHover(i + 1),
            click: () => this.pickRating(i + 1),
          });
        }
        return out;
      })(),
      ratingReaction: (() => {
        const r = this.state.rating; if (!r) return '';
        const level = r.hover || r.value;
        return ['', 'Not great', 'Could be better', "It's okay", 'Pretty good', 'Love it!'][level] || '';
      })(),
      ratingHasReaction: !!(this.state.rating && (this.state.rating.hover || this.state.rating.value)),
      ratingNoReaction: !(this.state.rating && (this.state.rating.hover || this.state.rating.value)),
      ratingSubmitDisabled: !(this.state.rating && this.state.rating.value),
      ratingSubmitStyle: (() => {
        const can = !!(this.state.rating && this.state.rating.value);
        const base = 'width:100%;height:40px;margin-top:14px;border:0;border-radius:10px;font-family:inherit;font-size:13px;font-weight:660;letter-spacing:-.01em;';
        return base + (can
          ? 'background:var(--accent-grad);color:var(--accent-contrast);cursor:pointer;'
          : 'background:var(--sunken);color:var(--text3);cursor:not-allowed;');
      })(),
      macroFormOpen: !!this.state.macroForm,
      macroFormActive: !!(this.state.macroForm && !this.state.macroForm.submitted),
      macroFormTitle: this.state.macroForm ? (this.state.macroForm.mode === 'bug' ? 'Report a bug' : 'Send feedback') : '',
      macroSubmitted: !!(this.state.macroForm && this.state.macroForm.submitted),
      macroText: this.state.macroForm ? this.state.macroForm.text : '',
      macroTypes: (() => {
        const cur = this.state.macroForm ? this.state.macroForm.type : 'suggestion';
        const base = 'flex:1;padding:9px 6px;border-radius:9px;font-family:inherit;font-size:12px;font-weight:600;cursor:pointer;transition:background .15s ease,color .15s ease,border-color .15s ease;';
        const mk = (key, label) => ({
          key, label, active: cur === key, pick: () => this.setMacroType(key),
          style: base + (cur === key
            ? 'background:color-mix(in srgb, var(--accent) 15%, transparent);color:var(--accent);border:1px solid transparent;'
            : 'background:transparent;color:var(--text2);border:1px solid var(--hair);'),
        });
        return [ mk('bug','Bug'), mk('suggestion','Suggestion'), mk('other','Other') ];
      })(),
      macroContextLabel: 'Auto-attached · ' + this.MODEL_VERSION + ' · ' + this.REVIT_VERSION + ' · current view',
      closeMacroForm: () => this.closeMacroForm(),
      setMacroText: (e) => this.setMacroText(e.target.value),
      submitMacroForm: () => this.submitMacroForm(),
      sugWalls: () => this.send('Create exterior walls on Level 2 along grid A–F'),
      sugSchedule: () => this.send('Generate a door schedule for Block A'),
      sugTag: () => this.send('Tag all rooms on Level 1 with name and number'),
    };
  }
}
<\u002Fscript>


<\u002Fbody><\u002Fhtml>"
  