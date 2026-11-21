import re,sys,json,html
def pages(path):
    t=open(path,encoding='utf8').read()
    out=[]
    for pg in t.split('<page ')[1:]:
        ws=[]
        for m in re.finditer(r'<word xMin="([\d.]+)" yMin="([\d.]+)" xMax="([\d.]+)" yMax="([\d.]+)">(.*?)</word>',pg):
            x0,y0,x1,y1=map(float,m.groups()[:4]); ws.append((x0,y0,x1,y1,html.unescape(m.group(5))))
        out.append(ws)
    return out
def lines(ws,tol=3.0):
    ws=sorted(ws,key=lambda w:(round((w[1]+w[3])/2),w[0]))
    L=[]
    for w in ws:
        yc=(w[1]+w[3])/2
        for l in L:
            if abs(l['y']-yc)<=tol: l['w'].append(w); break
        else: L.append({'y':yc,'w':[w]})
    for l in L: l['w'].sort(key=lambda w:w[0])
    return sorted(L,key=lambda l:l['y'])
def parse_page(ws):
    # LOi columns from header numbers 100..500
    cols={}
    for w in ws:
        if w[4] in ('100','200','300','400','500') and w[1]<280 and 200<w[0]<380:
            cols[int(w[4])]=(w[0]+w[2])/2
    cat=None; typ=None
    txt=' '.join(w[4] for w in sorted(ws,key=lambda w:(w[1],w[0])))
    m=re.search(r'Category\s*:\s*(.+?)\s+(?:Level of|Maklumat|Parameter Data)',txt)
    if m: cat=m.group(1).strip()
    m=re.search(r'Parameter Data\s*:\s*(\w+)',txt)
    if m: typ=m.group(1)
    return cols,cat,typ

def rows_of_page(ws):
    cols,cat,typ=parse_page(ws)
    if len(cols)<5: return cols,cat,typ,None
    left=cols[100]-14
    body_top=max([w[3] for w in ws if w[4] in ('100','200','300','400','500') and w[1]<280]+[0])
    names=[w for w in ws if w[2]<left and w[1]>body_top-2 and w[4]!='✓']
    ticks=[w for w in ws if w[4]=='✓' and w[1]>body_top-2]
    # name lines
    L=lines(names,tol=3.0)
    # cluster consecutive lines: gap between line y centers
    clusters=[]
    for l in L:
        if clusters and l['y']-clusters[-1]['ys'][-1] < 16.5:
            clusters[-1]['ys'].append(l['y']); clusters[-1]['lines'].append(l)
        else: clusters.append({'ys':[l['y']],'lines':[l]})
    rows=[]
    for c in clusters:
        text=''.join(' '.join(w[4] for w in l['w']) if i==0 else ('' if False else ' '+' '.join(w[4] for w in l['w'])) for i,l in enumerate(c['lines'])).strip()
        rows.append({'y0':c['ys'][0],'y1':c['ys'][-1],'name':text,'ticks':set()})
    for t in ticks:
        xc=(t[0]+t[2])/2; yc=(t[1]+t[3])/2
        loi=None
        k=min(cols,key=lambda k:abs(xc-cols[k]))
        if abs(xc-cols[k])<=10.5: loi=k
        if loi is None: continue
        best=min(rows,key=lambda r: 0 if r['y0']-6<=yc<=r['y1']+6 else min(abs(yc-r['y0']),abs(yc-r['y1'])))
        best['ticks'].add(loi)
    return cols,cat,typ,rows
