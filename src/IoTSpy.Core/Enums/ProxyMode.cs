namespace IoTSpy.Core.Enums;

public enum ProxyMode
{
    ExplicitProxy,
    ArpSpoof,
    GatewayRedirect,

    /// <summary>Legacy: passive is now <see cref="Models.ProxySettings.IsPassive"/>, which works with
    /// any mode. Still accepted on input (and read from old databases) and normalized to
    /// <see cref="ExplicitProxy"/> + <c>IsPassive</c>.</summary>
    [Obsolete("Use ProxySettings.IsPassive with any transport mode.")]
    Passive
}
