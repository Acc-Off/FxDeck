import type { KeyIcon } from "../shared/types";

/** Renders a key icon with the bundled icon fonts (design memo §3.8). */
export function Icon({ icon }: { icon: KeyIcon | null | undefined }) {
  if (!icon) return null;
  switch (icon.type) {
    case "mdi":
      return <span className={`icon mdi mdi-${icon.name}`} aria-hidden="true" />;
    case "fa":
      return <i className={`icon fa-${icon.style} fa-${icon.name}`} aria-hidden="true" />;
    case "emoji":
      return (
        <span className="icon emoji" aria-hidden="true">
          {icon.value}
        </span>
      );
    case "image":
      return <img className="icon image" src={`/api/deck/assets/${icon.hash}`} alt="" draggable={false} />;
    default:
      return null;
  }
}
