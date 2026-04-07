using Basis.BasisUI;
using Basis.Scripts.Device_Management;
using UnityEngine;

public class BasisOpenMenuForcefully : MonoBehaviour
{
    public bool OpenServerMenu = true;
    public void Start()
    {
#if UNITY_SERVER
        return;
#endif
        if(BasisDeviceManagement.OnInitializationComplete)
        {
            OpenMenu();
        }
        else
        {
            BasisDeviceManagement.OnInitializationCompleted += OpenMenu;
        }
    }
    public void OnDestroy()
    {
        BasisDeviceManagement.OnInitializationCompleted -= OpenMenu;
    }
    public void OpenMenu()
    {
#if UNITY_SERVER
        return;
#endif
        BasisMainMenu.Open();
        if (OpenServerMenu)
        {
            BasisMainMenu.OpenWithProvider(ServersProvider.TitleStatic);
        }
        else
        {
            BasisMainMenu.Open();
        }
    }
}
