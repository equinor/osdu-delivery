// The code a client's browser sign-in shows, as the device page reads it from a person: the control plane mints eight
// characters in two groups of four (`XXXX-XXXX`, DeviceCodeStore.NewUserCode) and compares them without the dash and
// whatever their case, so the page keeps what a person types or pastes to letters and digits, upper case, with the dash
// put back between the groups. Kept free of imports, so the unit suite reads it.

/** How many characters a code holds, and how many go before the dash. */
export const DEVICE_CODE_LENGTH = 8;
export const DEVICE_CODE_GROUP = 4;

/** The code's characters alone: letters and digits, upper case, no more than a code holds. */
export function deviceCodeCharacters(typed: string): string {
  return typed.toUpperCase().replace(/[^A-Z0-9]/g, "").slice(0, DEVICE_CODE_LENGTH);
}

/** What the field holds for what was typed: its characters, with the dash once the second group begins. */
export function formatDeviceCode(typed: string): string {
  const characters = deviceCodeCharacters(typed);
  return characters.length > DEVICE_CODE_GROUP
    ? `${characters.slice(0, DEVICE_CODE_GROUP)}-${characters.slice(DEVICE_CODE_GROUP)}`
    : characters;
}

/** Whether the field holds a whole code, which is what approving or denying one takes. */
export function isWholeDeviceCode(code: string): boolean {
  return deviceCodeCharacters(code).length === DEVICE_CODE_LENGTH;
}

/**
 * How many of the code's characters come before a caret at `position` in the formatted field: the box a caret there
 * types into next. The dash is no box, so a caret just after it types into the second group's first box.
 */
export function deviceCodeBoxAt(position: number): number {
  return position <= DEVICE_CODE_GROUP ? position : position - 1;
}
