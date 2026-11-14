import { useEffect, useState } from "react";
import { X } from "lucide-react";
import { api, type AppliesTo, type InvApp, type InvAppRow, type PageDetails } from "./api";
import { PrincipalPicker } from "./Inventory";

export const REVIEW_CHOICES = [1, 3, 6, 12, 24, 36];
export const EMPTY_DETAILS: PageDetails = { appliesTo: [], owner: null, reviewMonths: null };
const months = (n: number) => (n === 1 ? "1 month" : `${n} months`);
const sameDetails = (a: PageDetails, b: PageDetails) => JSON.stringify(a) === JSON.stringify(b);

/** The body of the details request: ids only. */
export const detailsBody = (d: PageDetails) => ({
  appliesTo: d.appliesTo.map((a) => ({ applicationId: a.applicationId, servers: a.servers })),
  ownerType: d.owner?.type ?? null, ownerId: d.owner?.id ?? null, reviewMonths: d.reviewMonths,
});
export const detailsChanged = (before: PageDetails | null | undefined, after: PageDetails) => !sameDetails(before ?? EMPTY_DETAILS, after);

/** When the page is due for review: its last update plus the cycle. */
const due = (updatedAt: string, m: number) => { const d = new Date(updatedAt); d.setMonth(d.getMonth() + m); return d.toLocaleDateString(undefined, { dateStyle: "medium" }); };

/**
 * A page's details, under its title at every width: what it applies to (applications of the inventory, and some of their
 * servers), who owns the process or escalation path it is about, and its review cycle. Shown when there is something to show;
 * edited with the page.
 */
export function PageDetailsPanel({ details, updatedAt, editing, value, onChange, onOpenApp }: {
  details: PageDetails | null | undefined; updatedAt: string; editing: boolean; value: PageDetails; onChange: (d: PageDetails) => void; onOpenApp?: (id: string) => void;
}) {
  if (editing) return <DetailsEditor value={value} onChange={onChange} />;
  const d = details ?? EMPTY_DETAILS;
  if (!d.appliesTo.length && !d.owner && !d.reviewMonths) return null;
  return (
    <dl className="page-details" aria-label="Page details">
      {d.appliesTo.length > 0 && (
        <>
          <dt>Applies to</dt>
          <dd className="tags">
            {d.appliesTo.map((a) => (
              <button key={a.applicationId} type="button" className="chip" disabled={!onOpenApp} onClick={() => onOpenApp?.(a.applicationId)}>
                {a.name}{a.servers.length ? ` · ${a.servers.join(", ")}` : ""}
              </button>
            ))}
          </dd>
        </>
      )}
      {d.owner && <><dt>Owner</dt><dd>{d.owner.name}</dd></>}
      {d.reviewMonths && <><dt>Review cycle</dt><dd>{months(d.reviewMonths)} · {due(updatedAt, d.reviewMonths)}</dd></>}
    </dl>
  );
}

function DetailsEditor({ value, onChange }: { value: PageDetails; onChange: (d: PageDetails) => void }) {
  const [apps, setApps] = useState<InvAppRow[]>([]);
  const [adding, setAdding] = useState("");
  const [servers, setServers] = useState<string[] | null>(null);
  const [pick, setPick] = useState<string[]>([]);
  useEffect(() => { void api.get<InvAppRow[]>("/inventory/apps").then((r) => setApps(Array.isArray(r) ? r : [])).catch(() => setApps([])); }, []);
  useEffect(() => {
    setPick([]);
    if (!adding) { setServers(null); return; }
    void api.get<InvApp>(`/inventory/apps/${adding}`).then((a) => setServers(a.installs?.map((i) => i.server) ?? [])).catch(() => setServers([]));
  }, [adding]);

  const set = (patch: Partial<PageDetails>) => onChange({ ...value, ...patch });
  function add() {
    const app = apps.find((a) => a.id === adding);
    if (!app) return;
    const entry: AppliesTo = { applicationId: app.id, name: app.name, servers: pick };
    set({ appliesTo: [...value.appliesTo.filter((a) => a.applicationId !== app.id), entry] });
    setAdding("");
  }

  return (
    <div className="page-details editing" aria-label="Page details">
      <span className="lbl">Applies to</span>
      <div className="applies-edit">
        <div className="tags">
          {value.appliesTo.map((a) => (
            <span key={a.applicationId} className="chip">{a.name}{a.servers.length ? ` · ${a.servers.join(", ")}` : ""}
              <button type="button" aria-label={`Remove ${a.name}`} onClick={() => set({ appliesTo: value.appliesTo.filter((x) => x.applicationId !== a.applicationId) })}><X className="ic sm" aria-hidden /></button>
            </span>
          ))}
        </div>
        <div className="applies-add">
          <select className="sel" aria-label="Application" value={adding} onChange={(e) => setAdding(e.target.value)}>
            <option value="" />
            {apps.filter((a) => !value.appliesTo.some((x) => x.applicationId === a.id)).map((a) => <option key={a.id} value={a.id}>{a.name}</option>)}
          </select>
          {servers && servers.length > 0 && (
            <span className="server-picks" role="group" aria-label="Servers">
              {servers.map((s) => (
                <label key={s} className="check"><input type="checkbox" className="chk" checked={pick.includes(s)}
                  onChange={(e) => setPick(e.target.checked ? [...pick, s] : pick.filter((x) => x !== s))} />{s}</label>
              ))}
            </span>
          )}
          <button type="button" className="tb" disabled={!adding} onClick={add}>Add</button>
        </div>
      </div>
      <span className="lbl" title="The team or person who owns the process or escalation path this page is about, not who wrote it.">Owner</span>
      <PrincipalPicker label="Owner" value={value.owner} onChange={(owner) => set({ owner })} />
      <span className="lbl">Review cycle</span>
      <select className="sel" aria-label="Review cycle" value={value.reviewMonths ?? ""} onChange={(e) => set({ reviewMonths: e.target.value ? Number(e.target.value) : null })}>
        <option value="">None</option>
        {REVIEW_CHOICES.map((m) => <option key={m} value={m}>{months(m)}</option>)}
      </select>
    </div>
  );
}
