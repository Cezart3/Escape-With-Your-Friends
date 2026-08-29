using UnityEngine;

namespace EscapeWithYourFriends.Combat
{
    /// <summary>
    /// Anything that can hold a <see cref="Carryable"/> on its person or in its cargo bay.
    ///
    /// <see cref="Carryable"/> used to reach for a <see cref="CarrySystem"/> by type, which quietly
    /// decided that only a character with arms can hold a body. The moment a dead friend has to ride
    /// in the back of a truck (#24) or in a boat (M5), that assumption costs either a fake CarrySystem
    /// bolted onto a vehicle or a second attach path with its own parenting and collision-ignore code.
    /// Both are worse than one interface.
    ///
    /// The contract is deliberately one property. Everything else about carrying — who may pick up
    /// what, the range check, the throw impulse, dropping on death — belongs to the holder and to
    /// <see cref="Carryable"/>, not here. A vehicle seat implements this and nothing more.
    /// </summary>
    public interface ICarryHolder
    {
        /// <summary>
        /// Where a carried body's hip is parented. Null means the holder is not currently able to
        /// hold anything, which <see cref="Carryable"/> treats as a refusal rather than an error.
        /// </summary>
        Transform CarrySocket { get; }
    }
}
