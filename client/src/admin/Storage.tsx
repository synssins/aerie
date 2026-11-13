import { Fragment, useCallback, useEffect, useState, type FormEvent, type ReactNode } from "react";
import { ArrowRight, ChevronRight, House, Plus } from "lucide-react";
import { api, ApiError, fmtDateTime, type StorageInfo, type StorageKind, type StorageMove, type StoragePlace } from "../api";
import { useDialogs } from "../Dialogs";
import { MenuButton } from "../ui";

/** The server's own words for a refusal; otherwise the short fallback. */
const problem = (e: unknown, fallback: string) => (e instanceof ApiError && e.body?.error) || fallback;

/**
 * Administration › Storage: where uploaded files are kept, and the move to another place.
 * Center: the places, or one place's form. Right: the selected place's or the move's details.
 */
export function Storage({ area, placeId, onSelect, onHome }: { area: string; placeId: string | null; onSelect: (id: string | null) => void; onHome: () => void }) {
  const [data, setData] = useState<StorageInfo | null>(null);
  const [adding, setAdding] = useState<StorageKind | null>(null);
  const load = useCallback(async () => { setData(await api.get<StorageInfo>("/admin/storage")); }, []);
  useEffect(() => { void load(); }, [load]);

  const copying = data?.move?.state === "Copying";
  useEffect(() => {
    if (!copying) return;
    const t = setInterval(() => void load(), 3000);
    return () => clearInterval(t);
  }, [copying, load]);

  const place = adding ? null : data?.places.find((p) => p.id === placeId) ?? null;
  const kindOf = (k: string): StorageKind => data?.kinds.find((x) => x.kind === k) ?? { kind: k, label: k, fields: [] };
  // A move outlives its places: one of them may have been removed since.
  const nameOf = (id: string) => data?.places.find((p) => p.id === id)?.name ?? "Removed place";
  const kind = adding ?? (place ? kindOf(place.kind) : null);
  const title = adding?.label ?? place?.name ?? "Storage";
  const back = () => { setAdding(null); onSelect(null); };

  return (
    <>
      <main className="doc wide">
        <div className="col">
          <nav className="crumb" aria-label="Breadcrumb">
            <button className="crumb-home" onClick={onHome} aria-label="Home" title="Home"><House className="ic" aria-hidden /></button><ChevronRight className="ic sm" aria-hidden /><span>{area}</span>
            {kind && <><ChevronRight className="ic sm" aria-hidden /><button className="crumb-link" onClick={back}>Storage</button><ChevronRight className="ic sm" aria-hidden /><b>{title}</b></>}
          </nav>
          <div className="titlebar">
            <h1>{title}</h1>
            <div className="ptools">
              {data && !kind && <MenuButton label="Add" icon={<Plus className="ic" aria-hidden />} items={data.kinds.map((k) => ({ label: k.label, onClick: () => setAdding(k) }))} />}
            </div>
          </div>

          {data?.move && <MovePanel move={data.move} from={nameOf(data.move.fromId)} to={nameOf(data.move.toId)} onChanged={load} />}

          {data && !kind && (
            <table className="grid-table">
              <thead><tr><th>Name</th><th>Kind</th><th>Path</th><th>Role</th></tr></thead>
              <tbody>
                {data.places.map((p) => (
                  <tr key={p.id} className="row" onClick={() => onSelect(p.id)}>
                    <td>{p.name}</td><td className="muted">{kindOf(p.kind).label}</td><td className="mono">{p.settings.path}</td><td>{p.role}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}

          {kind && (
            <PlaceForm key={place?.id ?? `new-${kind.kind}`} kind={kind} place={place}
              onSaved={async (p) => { await load(); if (adding) { setAdding(null); onSelect(p.id); } }}
              onLeft={async () => { await load(); back(); }} />
          )}
        </div>
      </main>

      <aside className="side" aria-label="Details">
        {place ? (
          <>
            <h3>Kind</h3><div>{kindOf(place.kind).label}</div>
            {kindOf(place.kind).fields.filter((f) => f.type !== "secret" && place.settings[f.key]).map((f) => (
              <Fragment key={f.key}><h3>{f.label}</h3><div className="mono">{place.settings[f.key]}</div></Fragment>
            ))}
            <h3>Role</h3><div>{place.role}</div>
            <h3>Created</h3><div>{fmtDateTime(place.createdAt)}</div>
          </>
        ) : !kind && data?.move ? (
          <>
            <h3>Started</h3><div>{fmtDateTime(data.move.startedAt)}</div>
            {data.move.finishedAt && <><h3>Finished</h3><div>{fmtDateTime(data.move.finishedAt)}</div></>}
          </>
        ) : null}
      </aside>
    </>
  );
}

/** The newest move: where from and to, how far it is, and what can be done in its state. */
function MovePanel({ move, from, to, onChanged }: { move: StorageMove; from: string; to: string; onChanged: () => Promise<void> }) {
  const dialogs = useDialogs();
  const [error, setError] = useState<string | null>(null);
  const open = move.state === "Copying" || move.state === "Copied";
  const ready = move.state === "Copied" && move.failed === 0 && move.copied === move.total && move.checked === move.total;

  async function run(work: () => Promise<unknown>) {
    setError(null);
    try { await work(); } catch (e) { setError(problem(e, "Not done.")); }
    await onChanged();
  }

  async function doSwitch() {
    if (await dialogs.confirm({ title: `Switch to ${to}?`, ok: "Switch", cancel: "Back" })) await run(() => api.post("/admin/storage/move/switch"));
  }

  async function cancel() {
    if (await dialogs.confirm({ title: `Cancel the move to ${to}?`, ok: "Cancel move", cancel: "Back", danger: true })) await run(() => api.post("/admin/storage/move/cancel"));
  }

  return (
    <section className="move" aria-label="Move">
      <div className="move-row"><b>{from}</b><ArrowRight className="ic sm" aria-hidden /><b>{to}</b><span className="muted">{move.state}</span></div>
      <div className="move-row counts">
        <span>Copied {move.copied} of {move.total}</span>
        <span>Checked {move.checked} of {move.total}</span>
        {move.failed > 0 && <span className="form-error">Failed {move.failed}</span>}
      </div>
      {move.lastError && <p className="form-error mono">{move.lastError}</p>}
      {open && (
        <div className="move-row">
          {move.state === "Copied" && <button type="button" className="tb" onClick={() => void run(() => api.post("/admin/storage/move/copy-again"))}>Copy again</button>}
          {move.state === "Copied" && (
            <span className="move-row">
              <button type="button" role="switch" aria-checked={move.preview} aria-labelledby="move-preview" className={`sw ${move.preview ? "on" : ""}`}
                onClick={() => void run(() => api.put("/admin/storage/move/preview", { on: !move.preview }))} />
              <span id="move-preview">Read my files from {to}</span>
            </span>
          )}
          <button type="button" className="tb primary" disabled={!ready} onClick={() => void doSwitch()}>Switch</button>
          <button type="button" className="tb" onClick={() => void cancel()}>Cancel</button>
        </div>
      )}
      {error && <p className="form-error" role="alert">{error}</p>}
    </section>
  );
}

/** A secret the server holds never comes here. null: it is set and stays as it is. A text: what to store instead (empty clears it). */
const secretsOf = (kind: StorageKind, place: StoragePlace | null): Record<string, string | null> =>
  Object.fromEntries(kind.fields.filter((f) => f.type === "secret").map((f) => [f.key, place?.secretsSet.includes(f.key) ? null : ""]));

/** One place, drawn from its kind's fields. A place that is not in use can be tested, moved to and removed. */
function PlaceForm({ kind, place, onSaved, onLeft }: { kind: StorageKind; place: StoragePlace | null; onSaved: (p: StoragePlace) => Promise<void>; onLeft: () => Promise<void> }) {
  const dialogs = useDialogs();
  const [name, setName] = useState(place?.name ?? "");
  const [values, setValues] = useState<Record<string, string>>(() =>
    Object.fromEntries(kind.fields.filter((f) => f.type !== "secret").map((f) => [f.key, place?.settings[f.key] ?? ""])));
  const [secrets, setSecrets] = useState(() => secretsOf(kind, place));
  const [errors, setErrors] = useState<Record<string, string[]>>({});
  const [status, setStatus] = useState<{ text: string; bad?: boolean } | null>(null);
  const [busy, setBusy] = useState(false);
  const [dirty, setDirty] = useState(false);

  const edit = (apply: () => void) => { apply(); setDirty(true); setStatus(null); };

  async function act(work: () => Promise<void>, failed: string) {
    setBusy(true);
    setStatus(null);
    try { await work(); } catch (e) { setStatus({ text: problem(e, failed), bad: true }); }
    finally { setBusy(false); }
  }

  async function save(e: FormEvent) {
    e.preventDefault();
    setErrors({});
    // An empty secret that was never set has nothing to clear.
    const sent = Object.fromEntries(Object.entries(secrets).map(([k, v]) => [k, v === "" && !place?.secretsSet.includes(k) ? null : v]));
    const body = { name, settings: values, secrets: sent };
    await act(async () => {
      try {
        const saved = place ? await api.put<StoragePlace>(`/admin/storage/places/${place.id}`, body) : await api.post<StoragePlace>("/admin/storage/places", { ...body, kind: kind.kind });
        setSecrets(secretsOf(kind, saved));
        setDirty(false);
        setStatus({ text: "Saved." });
        await onSaved(saved);
      } catch (err) {
        if (err instanceof ApiError && err.body?.errors) setErrors(err.body.errors); else throw err;
      }
    }, "Not saved.");
  }

  const test = () => act(async () => {
    const r = await api.post<{ ok: boolean; error: string | null }>(`/admin/storage/places/${place!.id}/test`);
    setStatus(r.ok ? { text: "Works" } : { text: r.error ?? "Failed.", bad: true });
  }, "Not tested.");

  const moveHere = () => act(async () => { await api.post("/admin/storage/move", { toId: place!.id }); await onLeft(); }, "Not started.");

  async function remove() {
    if (!await dialogs.confirm({ title: `Remove ${place!.name}?`, ok: "Remove", danger: true })) return;
    await act(async () => { await api.del(`/admin/storage/places/${place!.id}`); await onLeft(); }, "Not removed.");
  }

  const wrong = (key: string) => errors[key] && <p className="form-error wide" role="alert">{errors[key][0]}</p>;

  return (
    <form className="form" onSubmit={save}>
      <label className="lbl" htmlFor="st-name">Name</label>
      <input id="st-name" type="text" value={name} maxLength={100} onChange={(e) => edit(() => setName(e.target.value))} />
      {wrong("name")}
      {kind.fields.map((f) => f.type !== "secret" ? (
        <FieldRow key={f.key} label={f.label} id={`st-${f.key}`} error={wrong(f.key)}>
          <input id={`st-${f.key}`} type="text" value={values[f.key] ?? ""} spellCheck={false} onChange={(e) => edit(() => setValues((v) => ({ ...v, [f.key]: e.target.value })))} />
        </FieldRow>
      ) : secrets[f.key] === null ? (
        <FieldRow key={f.key} label={f.label} error={wrong(f.key)}>
          <span className="unit"><span>••••••••</span><button type="button" className="tb" onClick={() => edit(() => setSecrets((s) => ({ ...s, [f.key]: "" })))}>Change</button></span>
        </FieldRow>
      ) : (
        <FieldRow key={f.key} label={f.label} id={`st-${f.key}`} error={wrong(f.key)}>
          <input id={`st-${f.key}`} type="password" autoComplete="new-password" value={secrets[f.key] ?? ""} onChange={(e) => edit(() => setSecrets((s) => ({ ...s, [f.key]: e.target.value })))} />
        </FieldRow>
      ))}
      <div className="actions">
        <button type="submit" className="tb primary" disabled={busy || (place !== null && !dirty)}>Save</button>
        {place?.role === "Old" && (
          <>
            <button type="button" className="tb" disabled={busy || dirty} onClick={() => void test()}>Test</button>
            <button type="button" className="tb" disabled={busy || dirty} onClick={() => void moveHere()}>Move files here</button>
            <button type="button" className="tb" disabled={busy} onClick={() => void remove()}>Remove</button>
          </>
        )}
        {status && <span className={status.bad ? "form-error" : "muted"} role={status.bad ? "alert" : "status"}>{status.text}</span>}
      </div>
    </form>
  );
}

/** A label and its control in the form's two columns. No id: the control is not an input (a secret shown as dots). */
function FieldRow({ label, id, error, children }: { label: string; id?: string; error: ReactNode; children: ReactNode }) {
  return (
    <>
      {id ? <label className="lbl" htmlFor={id}>{label}</label> : <span className="lbl">{label}</span>}
      {children}
      {error}
    </>
  );
}
