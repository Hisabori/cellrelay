namespace CellRelay;
static class WindowCloseTests
{
    public static async Task Run(){
        foreach(bool background in new[]{false,true}){
            var completed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread=new Thread(()=>{
                Exception? failure=null;
                try{
                    using(var form=new MainForm(enableTray:background,persistSettings:false){ShowInTaskbar=false,Opacity=0}){
                        form.Shown+=(_,_)=>{
                            try{
                                form.Close();
                                if(background){
                                    if(form.IsDisposed||!form.IsInBackground)throw new Exception("Close did not retain hidden tray window");
                                    form.RestoreFromBackground();if(!form.Visible||form.IsInBackground)throw new Exception("Tray restore failed");
                                    form.ExitApplication();
                                }
                            }catch(Exception e){failure=e;form.ExitApplication();}
                        };
                        Application.Run(form);
                    }
                    if(failure!=null)completed.TrySetException(failure);else completed.TrySetResult();
                }catch(Exception e){completed.TrySetException(e);}
            }){IsBackground=true};
            thread.SetApartmentState(ApartmentState.STA);thread.Start();await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
