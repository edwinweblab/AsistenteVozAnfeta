using System;
using System.Runtime.InteropServices;

namespace Anfeta.UI.Services
{
    [ComImport, Guid("568b9108-44bf-40b4-9006-86afe5b5a620")]
    internal class CPolicyConfigClient { }

    [Guid("f8679f50-850a-41cf-9c72-430f290290c8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPolicyConfig
    {
        [PreserveSig]
        int GetMixFormat(string pszDeviceName, IntPtr ppFormat);

        [PreserveSig]
        int GetDeviceFormat(string pszDeviceName, bool bDefault, IntPtr ppFormat);

        [PreserveSig]
        int ResetDeviceFormat(string pszDeviceName);

        [PreserveSig]
        int SetDeviceFormat(string pszDeviceName, IntPtr pEndpointFormat, IntPtr MixFormat);

        [PreserveSig]
        int GetProcessingPeriod(string pszDeviceName, bool bDefault, IntPtr pmftDefaultPeriod, IntPtr pmftMinimumPeriod);

        [PreserveSig]
        int SetProcessingPeriod(string pszDeviceName, IntPtr pmftPeriod);

        [PreserveSig]
        int GetShareMode(string pszDeviceName, IntPtr pMode);

        [PreserveSig]
        int SetShareMode(string pszDeviceName, IntPtr mode);

        [PreserveSig]
        int GetPropertyValue(string pszDeviceName, bool bFxStore, IntPtr key, IntPtr pv);

        [PreserveSig]
        int SetPropertyValue(string pszDeviceName, bool bFxStore, IntPtr key, IntPtr pv);

        [PreserveSig]
        int SetDefaultEndpoint(string pszDeviceName, int role);

        [PreserveSig]
        int SetEndpointVisibility(string pszDeviceName, bool bVisible);
    }

    /// <summary>Permite cambiar dispositivo de audio predeterminado del sistema</summary>
    public class PolicyConfigClient
    {
        private readonly IPolicyConfig _policyConfig;

        public PolicyConfigClient()
        {
            _policyConfig = (IPolicyConfig)new CPolicyConfigClient();
        }

        /// <summary>Cambia dispositivo predeterminado (0=Console, 1=Multimedia, 2=Communications)</summary>
        public void SetDefaultEndpoint(string deviceId, int role = 1)
        {
            _policyConfig.SetDefaultEndpoint(deviceId, role);
        }
    }
}