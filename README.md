# aci-scaler

Azure Function to scale Container Instances based on Queue Length

This is very tailored to ImgBot's needs, but can be made more generic if it works out or there is a need.

Configuration required

```
CLIENT_ID=""
CLIENT_SECRET=""
TENANT_ID=""
SUBSCRIPTION_ID=""
RESOURCEGROUP_NAME=""
CONTAINERGROUP_NAME_SMALL=""
CONTAINERGROUP_NAME_MEDIUM=""
CONTAINERGROUP_NAME_LARGE=""
APPINSIGHTS_INSTRUMENTATIONKEY=""
GOOGLE_CREDENTIAL=""
GOOGLE_PROJECT=""
GOOGLE_ZONE=""
GOOGLE_INSTANCE=""
DAILYRESTART_NAME=""
```

`GOOGLE_CREDENTIAL` is the server-to-server string of json generated in the gcp console
