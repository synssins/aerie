import { Fragment, useCallback, useEffect, useState, type FormEvent, type ReactNode } from "react";
import { ChevronRight, House, Plus } from "lucide-react";
import { api, ApiError, fmtDateTime, type LogKind, type LogsInfo, type LogTarget } from "../api";
import { useDialogs } from "../Dialogs";
import { MenuButton } from "../ui";

const problem = (e: unknown, fallback: string) => (e instanceof ApiError && e.body?.error) || fallback;

/** Where a target sends: its first setting that is not a secret (the folder, the host, the address). */
const sendsTo = (kind: LogKind, t: LogTarget) => t.settings[kind.fields.find((f) => f.type !== "secret")?.key ?? ""] ?? "";

function stateOf(t: LogTarget, head: number): string {
  if (!t.enabled) return "Off";
  const behind = head - t.lastSequence;
  if (t.lastError) return `${behind} behind: ${t.lastError}`;
  return behind > 0 ? `${behind} behind` : "Up to date";
}

/**
 * Administration › Log shipping: where audit entries (sign-ins, token use, every change) are sent for a SIEM.
 * Center: the targets, or one target's form. Right: the selected target's details.
 */
export function LogShipping({ area, targetId, onSelect, onHome }: { area: string; targetId: string | null; onSelect: (id: string | null) => void; onHome: () => void }) {
  const [data, setData] = useState<LogsInfo | null>(null);
  const [adding, setAdding] = useState<LogKind | null>(null);
  const load = useCallback(async () => { setData(await api.get<LogsInfo>("/admin/logs")); }, []);
  useEffect(() => { void load(); }, [load]);

  const target = adding ? null : data?.targets.find((t) => t.id === targetId) ?? null;
  const kindOf = (k: string): LogKind => data?.kinds.find((x) => x.kind === k) ?? { kind: k, label: k, fields: [] };
  const kind = adding ?? (target ? kindOf(target.kind) : null);
  const title = adding?.label ?? target?.name ?? "Log shipping";
  const back = () => { setAdding(null); onSelect(null); };

  return (
    <>
      <main className="doc wide">
        <div className="col">
          <nav className="crumb" aria-label="Breadcrumb">
            <button className="crumb-home" onClick={onHome} aria-label="Home" title="Home"><House className="ic" aria-hidden /></button><ChevronRight className="ic sm" aria-hidden /><span>{area}</span>
            {kind && <><ChevronRight className="ic sm" aria-hidden /><button className="crumb-link" onClick={back}>Log shipping</button><ChevronRight className="ic sm" aria-hidden /><b>{title}</b></>}
          </nav>
          <div className="titlebar">
            <h1>{title}</h1>
            <div className="ptools">
              {data && !kind && <MenuButton label="Add" icon={<Plus className="ic" aria-hidden />} items={data.kinds.map((k) => ({ label: k.label, onClick: () => setAdding(k) }))} />}
            </div>
          </div>

          {data && !kind && (
            <table className="grid-table">
              <thead><tr><th>Name</th><th>Kind</th><th>Sends to</th><th>State</th></tr></thead>
              <tbody>
                {data.targets.map((t) => (
                  <tr key={t.id} className={`row ${t.enabled ? "" : "off"}`} onClick={() => onSelect(t.id)}>
                    <td>{t.name}</td><td className="muted">{kindOf(t.kind).label}</td><td className="mono">{sendsTo(kindOf(t.kind), t)}</td>
                    <td className={t.enabled && t.lastError ? "form-error" : ""}>{stateOf(t, data.head)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}

          {kind && (
            <TargetForm key={target?.id ?? `new-${kind.kind}`} kind={kind} target={target}
              onSaved={async (t) => { await load(); if (adding) { setAdding(null); onSelect(t.id); } }}
              onRemoved={async () => { await load(); back(); }} />
          )}
        </div>
      </main>

      <aside className="side" aria-label="Details">
        {target && data && (
          <>
            <h3>Kind</h3><div>{kindOf(target.kind).label}</div>
            {kindOf(target.kind).fields.filter((f) => f.type !== "secret" && target.settings[f.key]).map((f) => (
              <Fragment key={f.key}><h3>{f.label}</h3><div className="mono">{target.settings[f.key]}</div></Fragment>
            ))}
            <h3>State</h3><div>{stateOf(target, data.head)}</div>
            <h3>Last sent</h3><div>{target.lastShippedAt ? fmtDateTime(target.lastShippedAt) : "Never"}</div>
            <h3>Created</h3><div>{fmtDateTime(target.createdAt)}</div>
          </>
        )}
      </aside>
    </>
  );
}

/** A secret the server holds never comes here. null: it is set and stays as it is. A text: what to store instead (empty clears it). */
const secretsOf = (kind: LogKind, target: LogTarget | null): Record<string, string | null> =>
  Object.fromEntries(kind.fields.filter((f) => f.type === "secret").map((f) => [f.key, target?.secretsSet.includes(f.key) ? null : ""]));

function TargetForm({ kind, target, onSaved, onRemoved }: { kind: LogKind; target: LogTarget | null; onSaved: (t: LogTarget) => Promise<void>; onRemoved: () => Promise<void> }) {
  const dialogs = useDialogs();
  const [name, setName] = useState(target?.name ?? "");
  const [enabled, setEnabled] = useState(target?.enabled ?? true);
  const [values, setValues] = useState<Record<string, string>>(() =>
    Object.fromEntries(kind.fields.filter((f) => f.type !== "secret").map((f) => [f.key, target?.settings[f.key] ?? (f.type === "select" ? f.options?.[0] ?? "" : "")])));
  const [secrets, setSecrets] = useState(() => secretsOf(kind, target));
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
    const sent = Object.fromEntries(Object.entries(secrets).map(([k, v]) => [k, v === "" && !target?.secretsSet.includes(k) ? null : v]));
    const settings = Object.fromEntries(Object.entries(values).filter(([, v]) => v !== ""));
    const body = { name, enabled, settings, secrets: sent };
    await act(async () => {
      try {
        const saved = target ? await api.put<LogTarget>(`/admin/logs/targets/${target.id}`, body) : await api.post<LogTarget>("/admin/logs/targets", { ...body, kind: kind.kind });
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
    const r = await api.post<{ ok: boolean; error: string | null }>(`/admin/logs/targets/${target!.id}/test`);
    setStatus(r.ok ? { text: "Works" } : { text: r.error ?? "Failed.", bad: true });
  }, "Not tested.");

  async function remove() {
    if (!await dialogs.confirm({ title: `Remove ${target!.name}?`, ok: "Remove", danger: true })) return;
    await act(async () => { await api.del(`/admin/logs/targets/${target!.id}`); await onRemoved(); }, "Not removed.");
  }

  const wrong = (key: string) => errors[key] && <p className="form-error wide" role="alert">{errors[key][0]}</p>;

  return (
    <form className="form" onSubmit={save}>
      <label className="lbl" htmlFor="lg-name">Name</label>
      <input id="lg-name" type="text" value={name} maxLength={100} onChange={(e) => edit(() => setName(e.target.value))} />
      {wrong("name")}
      {kind.fields.map((f) => f.type === "select" ? (
        <Row key={f.key} label={f.label} id={`lg-${f.key}`} error={wrong(f.key)}>
          <select id={`lg-${f.key}`} className="sel" value={values[f.key] ?? ""} onChange={(e) => edit(() => setValues((v) => ({ ...v, [f.key]: e.target.value })))}>
            {(f.options ?? []).map((o) => <option key={o} value={o}>{o}</option>)}
          </select>
        </Row>
      ) : f.type !== "secret" ? (
        <Row key={f.key} label={f.label} id={`lg-${f.key}`} error={wrong(f.key)}>
          <input id={`lg-${f.key}`} type="text" value={values[f.key] ?? ""} spellCheck={false} onChange={(e) => edit(() => setValues((v) => ({ ...v, [f.key]: e.target.value })))} />
        </Row>
      ) : secrets[f.key] === null ? (
        <Row key={f.key} label={f.label} error={wrong(f.key)}>
          <span className="unit"><span>••••••••</span><button type="button" className="tb" onClick={() => edit(() => setSecrets((s) => ({ ...s, [f.key]: "" })))}>Change</button></span>
        </Row>
      ) : (
        <Row key={f.key} label={f.label} id={`lg-${f.key}`} error={wrong(f.key)}>
          <input id={`lg-${f.key}`} type="password" autoComplete="new-password" value={secrets[f.key] ?? ""} onChange={(e) => edit(() => setSecrets((s) => ({ ...s, [f.key]: e.target.value })))} />
        </Row>
      ))}
      <span className="lbl" id="lg-enabled">Enabled</span>
      <button type="button" role="switch" aria-checked={enabled} aria-labelledby="lg-enabled" className={`sw ${enabled ? "on" : ""}`} onClick={() => edit(() => setEnabled(!enabled))} />
      <div className="actions">
        <button type="submit" className="tb primary" disabled={busy || (target !== null && !dirty)}>Save</button>
        {target && (
          <>
            <button type="button" className="tb" disabled={busy || dirty} onClick={() => void test()}>Test</button>
            <button type="button" className="tb" disabled={busy} onClick={() => void remove()}>Remove</button>
          </>
        )}
        {status && <span className={status.bad ? "form-error" : "muted"} role={status.bad ? "alert" : "status"}>{status.text}</span>}
      </div>
    </form>
  );
}

function Row({ label, id, error, children }: { label: string; id?: string; error: ReactNode; children: ReactNode }) {
  return (
    <>
      {id ? <label className="lbl" htmlFor={id}>{label}</label> : <span className="lbl">{label}</span>}
      {children}
      {error}
    </>
  );
}
