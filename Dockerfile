FROM mcr.microsoft.com/dotnet/aspnet:7.0
WORKDIR /app 
EXPOSE 443  

COPY ./app .
ENTRYPOINT ["dotnet", "VLink.Private.GrpcRoomServer.dll"]
