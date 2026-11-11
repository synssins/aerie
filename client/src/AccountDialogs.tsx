import { useEffect, useRef } from "react";
import type { Me } from "./api";

export function AccountDialog({ me, onClose }: { me: Me; onClose: () => void }) {
  const ref = useRef<HTMLDialogElement>(null);
  useEffect(() => { ref.current?.showModal(); }, []);
  return (
    <dialog ref={ref} className="dlg" onCancel={(e) => { e.preventDefault(); onClose(); }} aria-labelledby="acct-title">
      <h2 id="acct-title">Account</h2>
      <dl className="kv">
        <dt>Name</dt><dd>{me.displayName}</dd>
        <dt>User name</dt><dd>{me.userName}</dd>
        <dt>Role</dt><dd>{me.isAdmin ? "Administrator" : "User"}</dd>
      </dl>
      <div className="dlg-actions"><button type="button" className="tb primary" onClick={onClose} autoFocus>Done</button></div>
    </dialog>
  );
}
