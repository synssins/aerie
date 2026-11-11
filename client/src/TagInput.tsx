import { useState, type KeyboardEvent } from "react";
import { X } from "lucide-react";

/** Chip-style tag entry: Enter or comma adds, Backspace on empty removes the last. */
export function TagInput({ value, onChange, id }: { value: string[]; onChange: (t: string[]) => void; id?: string }) {
  const [text, setText] = useState("");

  function add(raw: string) {
    const t = raw.trim().toLowerCase().slice(0, 50);
    if (t && !value.includes(t) && value.length < 20) onChange([...value, t]);
    setText("");
  }

  function onKey(e: KeyboardEvent<HTMLInputElement>) {
    if (e.key === "Enter" || e.key === ",") { e.preventDefault(); add(text); }
    else if (e.key === "Backspace" && !text && value.length) onChange(value.slice(0, -1));
  }

  return (
    <div className="tag-input">
      {value.map((t) => (
        <span key={t} className="chip">{t}<button type="button" aria-label={`Remove ${t}`} onClick={() => onChange(value.filter((x) => x !== t))}><X className="ic sm" /></button></span>
      ))}
      <input id={id} value={text} onChange={(e) => setText(e.target.value.replace(",", ""))} onKeyDown={onKey} onBlur={() => text && add(text)}
        placeholder={value.length ? "" : "Add tags"} aria-label="Tags" />
    </div>
  );
}
