import { useEffect, useState } from "react";
import { api } from "../api";
import { Seg } from "../SettingsView";

type RequiredFor = "admins" | "everyone";

/** Administration › Security › Sign-in: who must use one-time codes. Admins always do. */
export function CodeSettingsPanel() {
  const [value, setValue] = useState<RequiredFor | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => { void api.get<{ requiredFor: RequiredFor }>("/admin/settings/codes").then((s) => setValue(s.requiredFor)); }, []);
  if (!value) return null;

  async function change(v: RequiredFor) {
    setError(null);
    try { await api.put("/admin/settings/codes", { requiredFor: v }); setValue(v); } catch { setError("Not saved."); }
  }

  return (
    <div className="form">
      <span className="lbl">Require codes for</span>
      <Seg label="Require codes for" value={value} onChange={(v) => void change(v)} options={[["admins", "Admins"], ["everyone", "Everyone"]]} />
      {error && <p className="form-error" role="alert">{error}</p>}
    </div>
  );
}
